# AngelEye 橋接應用

本專案包含兩套 Angel Eye II-EX 橋接程式：

- `AngelEyeBmsBridge/`：Windows 11 GUI 版，用於現場連線、診斷與接收測試。
- `AngelEyeBridgeWorker/`：Ubuntu headless worker，用於正式環境長時間背景執行。

目前設計重點是先確認牌盒資料能穩定進入橋接程式，再視需要開啟 BMS 傳送。

## 目前安全狀態

- BMS 傳送預設關閉。
- Ubuntu worker 範例設定中 `bridge.readOnly` 為 `true`。
- 各桌 `bmsTransmitEnabled` 預設為 `false`。
- read-only 模式下不會對牌盒送 OP 控制指令。
- 診斷時以接收資料、顯示 `RXRAW` / `RX` / `EVENT` 為主。

## MOXA 對應

| 桌台 | MOXA IP | TCP Port | Serial |
|---|---|---:|---|
| 901 | `10.5.32.24` | `4001` | `4800, 8, None, 1, Flow None` |
| 902 | `10.5.32.25` | `4001` | `4800, 8, None, 1, Flow None` |
| 903 | `10.5.32.26` | `4001` | `4800, 8, None, 1, Flow None` |
| QA | `10.5.32.124` | `4001` | `9600, 8, None, 1, Flow None` |

Angel Eye II-EX 通訊規格書預設為：

```text
RS-232C
4800 bps
8 data bits
Parity None
1 stop bit
Flow Control None
```

因此 QA 若也是 Angel Eye II-EX，需另外確認為什麼 baud rate 為 `9600`。

## 診斷判讀

程式 TCP 連上 MOXA 後，只要收到任何 byte，就會先記錄：

```text
RXRAW
```

判讀方式：

- 有 `Connected to MOXA TCP ...`：代表 VM / 主機已連上 MOXA TCP port。
- 有 `RXRAW`：代表 MOXA 已經有資料轉到橋接程式。
- 有 `RX`：代表收到完整封包。
- 有 `EVENT`：代表封包已解析成事件，例如抽牌、結果、錯誤或切牌。

若只有 connected，沒有 `RXRAW`，代表 TCP socket 連線成功，但橋接程式沒有收到 MOXA 送出的任何資料。

## 現場檢查流程

1. 確認主機能連到 MOXA TCP port。

   ```powershell
   Test-NetConnection 10.5.32.25 -Port 4001
   ```

   若顯示：

   ```text
   TcpTestSucceeded : True
   ```

   代表主機到 MOXA TCP `4001` 是通的。

2. 用 PowerShell 監聽 60 秒是否收到原始資料。

   ```powershell
   $ip="10.5.32.25";$port=4001;$seconds=60;$client=[System.Net.Sockets.TcpClient]::new();$client.Connect($ip,$port);Write-Host "Connected to $ip`:$port, waiting $seconds seconds...";$stream=$client.GetStream();$buffer=New-Object byte[] 4096;$end=(Get-Date).AddSeconds($seconds);while((Get-Date)-lt $end){if($stream.DataAvailable){$count=$stream.Read($buffer,0,$buffer.Length);$hex=($buffer[0..($count-1)]|ForEach-Object{$_.ToString("X2")})-join " ";Write-Host "RXRAW $count bytes: $hex"};Start-Sleep -Milliseconds 100};$client.Close();Write-Host "Done"
   ```

3. 同步查看 MOXA Web UI。

   ```text
   http://<MOXA IP>
   Monitor > Async
   ```

   觀察 `RxCnt` / `RxTotalCnt` 是否在現場發牌或抽牌時增加。

## 問題定位

| 現象 | 判斷 |
|---|---|
| TCP 測試不通 | 主機到 MOXA 網路或 port 不通 |
| TCP 通，但 PowerShell / GUI 沒 `RXRAW`，且 MOXA `RxCnt` 沒增加 | 牌盒資料沒有進 MOXA serial 端 |
| TCP 通，MOXA `RxCnt` 有增加，但 PowerShell / GUI 沒 `RXRAW` | MOXA 有收到 serial，但沒有轉到 TCP client |
| 有 `RXRAW`，但沒有 `RX` / `EVENT` | 程式收到資料，但封包格式或 parser 需調整 |

## 程式結構

### Windows GUI

位置：

```text
AngelEyeBmsBridge/
```

重點檔案：

- `SerialListener.cs`：Serial / MOXA TCP 接收、`RXRAW` 診斷、封包 buffer 處理。
- `AngelEyeProtocol.cs`：Angel Eye II-EX 指令與 BCC 工具。
- `ShoeEndpoint.cs`：單一牌盒端點狀態、事件轉換與本地局號。
- `BridgeSettings.cs`：GUI 持久化設定與預設桌台。

### Ubuntu Worker

位置：

```text
AngelEyeBridgeWorker/
```

重點檔案：

- `AngelBridgeWorker.cs`：背景服務主流程、多桌連線、事件佇列與 BMS outbox。
- `WorkerSettings.cs`：設定檔讀取與驗證。
- `appsettings.example.json`：正式設定範例。
- `moxa-endpoints.pit9.json`：PIT9 MOXA 對應與已知設定。

## Angel Eye II-EX 封包摘要

Angel Eye II-EX 抽牌等事件會主動送 interrupt telegram：

```text
ENQ  Seq  Data  ETX  BCC
05   31   ...   03   ...
```

常見 interrupt code：

| Code | 意義 |
|---|---|
| `S` | Start of Communication |
| `P` | Stand By |
| `G` | Game Result |
| `D` | Card Drawing |
| `d` | Card Drawing outside active game |
| `R` | Card Drawing retransmission |
| `E` | Error occurred |
| `e` | Error cancelled |
| `L` | Lock status changed |
| `M` | Preset value changed |

## 部署提醒

- 同一台 MOXA 測試時，盡量只開一個 TCP client，避免資料被其他連線消耗或造成判讀混亂。
- QA 目前 Max connection 為 `1`，更需要避免同時連線。
- 若 `RxTotalCnt` 有歷史累計，不代表當下仍有資料；測試時應同步比較發牌前後的差值。
- 正式開啟 BMS 傳送前，請確認每桌 `RXRAW`、`RX`、`EVENT` 都正常。
