# ANGEL EYE Bridge Rocky Linux Service

## 架構

一台 Rocky Linux 建議只跑一個 `angel-eye-bridge.service`。
這個 service 讀取一份 `/etc/angel-eye-bridge/appsettings.json`，在同一個進程內管理多張桌 / 多個 NPort 5110。

三台 TeleBet VM 使用同一套程式，各自放對應角色的設定檔；服務名稱都使用 `angel-eye-bridge.service`。

Windows `AngelEyeBmsBridge.exe` 無參數時是唯讀 Query Console；只有明確加上 `--engineering` 才進入原工程介面。Query Console 的 Worker 查詢頁不開啟 Worker journal，也不送 BMS POST；「MOXA 即時監看」只有操作員明確按開始後才建立額外的 receive-only 連線。

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
sudo cp appsettings.server-29.qa.example.json /etc/angel-eye-bridge/appsettings.json
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

## PIT9 設定檔

目前套件內有三份 Midori / PIT9 設定範本：

- `appsettings.server-29.qa.example.json`：`10.5.32.29` QA；送往 `redhood67.infinitybeyonder888test.com`。包含 901 / 902 / 903 / QA，目前只有 901 開啟 BMS 傳送。
- `appsettings.server-30.production.example.json`：`10.5.32.30` 正式主機；送往 `redhood67.infinitybeyonder888.com`。包含 901 / 902 / 903。
- `appsettings.server-31.standby.example.json`：`10.5.32.31` 正式備援；設定與正式主機相同，但 systemd 平時必須保持停止與停用。

正式與備援範本的 JWT Provider ID、Token Serial、Signing Key 及 SourceDataId 尚未取得，因此保留 `REPLACE_WITH_...` 或空白。部署前必須依正式 BMS 資料補齊；桌台主要對應欄位仍是 `sourceDataCode`（901 / 902 / 903）。

目前已確認的 MOXA 對應另記錄於 `moxa-endpoints.pit9.json`：

```text
901 -> 10.5.32.24:4001, NPort 5110, 4800 8N1 None
902 -> 10.5.32.25:4001, NPort 5110, 4800 8N1 None
903 -> 10.5.32.26:4001, NPort 5110, 4800 8N1 None
QA  -> 10.5.32.124:4001, NPort 5110, 9600 8N1 None
```

部署對應：

- `10.5.32.29`：複製 `appsettings.server-29.qa.example.json` 為 `/etc/angel-eye-bridge/appsettings.json`。
- `10.5.32.30`：複製 `appsettings.server-30.production.example.json` 為 `/etc/angel-eye-bridge/appsettings.json`，正式啟用服務。
- `10.5.32.31`：複製 `appsettings.server-31.standby.example.json` 為 `/etc/angel-eye-bridge/appsettings.json`，但執行 `sudo systemctl disable --now angel-eye-bridge` 保持備援停止。

備援切換時，必須先停止 `10.5.32.30` 的服務，再啟用 `10.5.32.31`，避免兩台同時讀取相同牌盒並重複送事件。

同一張桌同一時間只能有一個 **Worker／Bridge sender**，避免 BMS 收到重複牌訊。NPort 現場已確認可同時接受 4 條連線，因此可另有 Query Console 的唯讀監看連線；監看連線不會送牌盒命令、寫 SQLite 或 POST BMS。

## Query Console 的 MOXA 即時監看

「MOXA 即時監看」與 Worker 查詢是兩條獨立資料來源：

- 程式啟動時不會自動連 MOXA；先選取 901、902、903 或 QA，再按「開始監看選取桌台」。
- 每個 Query Console process 對每個 endpoint 最多使用 1 條連線。四桌全部開始時共使用 4 條；同一桌重複按開始不會增加 socket。
- endpoint 固定為 901 `10.5.32.24:4001`、902 `10.5.32.25:4001`、903 `10.5.32.26:4001`、QA `10.5.32.124:4001`，GUI 不提供任意主機設定。
- 頁面固定顯示「MOXA 直連／session-local／不送 BMS」。它只接收與解析 frame，沒有 write、Lock、Unlock、ClearError、GP、重送、journal 或 BMS client。
- `Partial` 表示本次 session 可能從牌局中途加入、曾漏序號或剛完成重連；這時牌面只能當現場診斷線索，不可當正式完整牌局。收到可判定的切牌邊界並保持連續後，才顯示「連續」。
- `Session age` 是本次按開始後的時間，`Last frame` 是最近收到資料的時間；RX 只保留最近 200 筆，`Dropped` 顯示被淘汰的舊列數。
- 不使用時選取桌台並按「停止監看選取桌台」。關閉 Query Console 會停止並釋放全部監看連線。
- Worker 仍是唯一 BMS sender。Worker 頁面的靴／局／Event ID／Outbox 證據，不會由 MOXA monitor 建立或修改。

若監看功能影響現場連線額度，可立即停止該桌監看；不需停止 Worker。若要回復舊 GUI，替換回舊版 Windows exe 即可，monitor 沒有資料庫 migration 或持久化資料需要回復。

## Query Console migration 上線順序

每台 VM 上線前都要保存「程式、設定、SQLite、靴局狀態」四項備份。以下命令中的時間戳應改成實際變更單號或時間；資料庫複製前先停止服務，確保備份是一致快照。

```bash
sudo systemctl stop angel-eye-bridge
sudo mkdir -p /var/backups/angel-eye-bridge/20260723-query-console
sudo cp -a /opt/angel-eye-bridge /var/backups/angel-eye-bridge/20260723-query-console/opt
sudo cp -a /etc/angel-eye-bridge /var/backups/angel-eye-bridge/20260723-query-console/etc
sudo cp -a /var/lib/angel-eye-bridge/bridge-events.sqlite /var/backups/angel-eye-bridge/20260723-query-console/bridge-events.sqlite
sudo cp -a /var/lib/angel-eye-bridge/bridge-state.json /var/backups/angel-eye-bridge/20260723-query-console/bridge-state.json
```

若狀態檔尚不存在，可略過該檔但必須在變更紀錄註明。不要只備份 SQLite 的 WAL 檔；停服務後備份主資料庫檔。

上線順序：

1. **QA `.29`**：套用 `appsettings.server-29.qa.example.json` 對應設定，執行 `--check-config`，啟動後驗證 `/health`、`/api/v1/status`、牌局查詢、缺結果、retry 與 RecoverRound。Query Console 使用 `QA .29` profile。
2. **正式主用 `.30`**：確認 `.31` 為 `disabled/inactive`，備份 `.30` 後更新程式與設定，執行 `--check-config`，再啟動並驗證三桌與 outbox。Query Console 使用 `正式主用 .30` profile。
3. **正式備援 `.31`**：只更新程式與設定並執行 `--check-config`；最後必須再次執行 `sudo systemctl disable --now angel-eye-bridge`。`.31` 的 Query Console profile 只有在 standby Worker 已因正式切換而啟動時才會有即時資料，建立 SSH tunnel 本身不得啟動 sender。

每一步都必須核對 `/api/v1/status` 的 `instanceName`、`environment`、`role`，避免把 `.29/.30/.31` 資料來源看錯。HTTP 傳送成功只代表 `BMS endpoint accepted`，不代表 BMS 後續流程已最終入庫。

## Rollback

回復前先停止目標 VM 的 service。舊版 Worker 會忽略新增的 projection/audit tables，因此一般優先回復舊程式與舊設定、保留目前 SQLite，以免遺失上線後收到的事件；只有確認 schema／資料檔本身造成故障、且已匯出變更期間事件時，才用備份 SQLite 覆蓋。

```bash
sudo systemctl stop angel-eye-bridge
sudo cp -a /var/backups/angel-eye-bridge/20260723-query-console/opt/. /opt/angel-eye-bridge/
sudo cp -a /var/backups/angel-eye-bridge/20260723-query-console/etc/. /etc/angel-eye-bridge/
# 僅在變更負責人確認可接受遺失上線後事件時才執行下一行：
# sudo cp -a /var/backups/angel-eye-bridge/20260723-query-console/bridge-events.sqlite /var/lib/angel-eye-bridge/bridge-events.sqlite
/opt/angel-eye-bridge/angel-eye-bridge --config /etc/angel-eye-bridge/appsettings.json --check-config
```

- 回復 `.29` 或 `.30`：config check 通過後才 `sudo systemctl start angel-eye-bridge`，再驗證 `/health` 與 outbox。
- 回復 `.31`：完成後仍執行 `sudo systemctl disable --now angel-eye-bridge`；除非走正式主備切換程序，不得啟動。
- 正式主備切換：先在 `.30` 執行 `disable --now` 並確認 inactive，之後才能在 `.31` 執行 `enable --now`。切回同理，任何時刻 `.30/.31` 不得同時送資料。
- Windows GUI 回復：換回舊版 exe 即可；GUI 沒有 database migration，也不得用 GUI 觸發補送或主備切換。

## 設定重點

`bridge.connectionMode` 是整台 Bridge 共用設定：

- `MoxaTcp`：Bridge 直接連 NPort IP:Port，正式場建議用這個。
- `ComPort`：使用 OS 映射出的 COM/tty serial port。

每張桌只填自己的端點：

```json
{
  "deskName": "901桌",
  "sourceDataCode": "901",
  "shoeId": "SHOE901",
  "currentShoe": 0,
  "currentRound": 1,
  "moxaHost": "10.5.32.24",
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

查詢 API 與 health 共用 loopback listener，預設不直接開放到網路。從 Windows 查詢台連入 `.29`、`.30` 或 `.31` 時，先建立 SSH tunnel：

```powershell
# QA .29
ssh -p 53229 -N -L 18080:127.0.0.1:18080 telbet@10.5.32.29

# 正式主用 .30（本機改用 18081，避免與 QA profile 衝突）
ssh -p 53229 -N -L 18081:127.0.0.1:18080 telbet@10.5.32.30

# 正式備援 .31（只查詢；不得因此啟動 standby Worker sender）
ssh -p 53229 -N -L 18082:127.0.0.1:18080 telbet@10.5.32.31
```

Query Console profile 對應 `http://127.0.0.1:18080`、`:18081`、`:18082`。未完成 TLS 與身分驗證前，不可把 `health.host` 改成 `0.0.0.0` 或一般網卡位址。

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
