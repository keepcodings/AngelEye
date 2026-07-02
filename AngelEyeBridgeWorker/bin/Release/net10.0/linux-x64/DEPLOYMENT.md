# ANGEL EYE Bridge Ubuntu Service

## 架構

一台 Ubuntu 建議只跑一個 `angel-eye-bridge.service`。  
這個 service 讀取一份 `/etc/angel-eye-bridge/appsettings.json`，在同一個進程內管理多張桌 / 多個 NPort 5110。

多台 Ubuntu 不需要改程式，只要每台放自己的設定檔：

- Ubuntu-A：設定 901、902
- Ubuntu-B：設定 903、904
- 服務名稱都可以是 `angel-eye-bridge.service`

## 目錄

```bash
/opt/angel-eye-bridge/                 # 程式
/etc/angel-eye-bridge/appsettings.json # 設定
/var/lib/angel-eye-bridge/             # SQLite outbox
/var/lib/angel-eye-bridge/bridge-state.json # 靴局狀態
```

## 安裝

```bash
sudo useradd --system --home /var/lib/angel-eye-bridge --shell /usr/sbin/nologin angelbridge
sudo mkdir -p /opt/angel-eye-bridge /etc/angel-eye-bridge /var/lib/angel-eye-bridge
sudo chown -R angelbridge:angelbridge /var/lib/angel-eye-bridge
sudo cp -r publish/* /opt/angel-eye-bridge/
sudo cp appsettings.example.json /etc/angel-eye-bridge/appsettings.json
sudo cp systemd/angel-eye-bridge.service /etc/systemd/system/angel-eye-bridge.service
sudo systemctl daemon-reload
sudo systemctl enable angel-eye-bridge
```

修改 `/etc/angel-eye-bridge/appsettings.json` 後先檢查：

```bash
/opt/angel-eye-bridge/angel-eye-bridge --config /etc/angel-eye-bridge/appsettings.json --check-config
```

啟動：

```bash
sudo systemctl start angel-eye-bridge
```

## 設定重點

`bridge.connectionMode` 是整台 Bridge 共用設定：

- `MoxaTcp`：Bridge 直接連 NPort IP:Port，正式場建議用這個。
- `ComPort`：使用 OS 映射出的 COM/tty serial port。

每張桌只填自己的端點：

```json
{
  "deskName": "901桌",
  "sourceDataCode": "ANGEL_BAC901",
  "shoeId": "SHOE901",
  "currentShoe": 0,
  "currentRound": 1,
  "moxaHost": "10.5.32.25",
  "moxaPort": 4001
}
```

`currentShoe = 0` 代表啟動時自動用當天日期產生第一靴，例如 `202606300001`。  
`currentRound = 1` 代表啟動時從第 1 局開始送 `StartGame`。

服務啟動後，目前靴號 / 局號會寫入 `/var/lib/angel-eye-bridge/bridge-state.json`。  
systemd 重啟時會優先套用狀態檔，不會每次都回到 appsettings 的初始局號。

## 日誌查詢

即時看：

```bash
journalctl -u angel-eye-bridge -f
```

看最近 200 行：

```bash
journalctl -u angel-eye-bridge -n 200 --no-pager
```

只看今天：

```bash
journalctl -u angel-eye-bridge --since today --no-pager
```

## 健康檢查

預設本機開 `127.0.0.1:18080`：

```bash
curl http://127.0.0.1:18080/health
```

HTTP 200 代表所有啟用的端點都已連線。  
HTTP 503 代表至少一張啟用桌未連線；回傳 JSON 會列出每張桌的連線、靴局、最後事件與 outbox 待送數。

## 常用操作

```bash
sudo systemctl status angel-eye-bridge
sudo systemctl restart angel-eye-bridge
sudo systemctl stop angel-eye-bridge
```

改設定後：

```bash
sudo systemctl restart angel-eye-bridge
```

## 打包

```bash
dotnet publish AngelEyeBridgeWorker/AngelEyeBridgeWorker.csproj -c Release -r linux-x64 --self-contained true -o publish
```
