# FastInput 檔案暫存區：介面規格（後端與前端共同遵守）

手機與電腦都能把檔案丟進「暫存區」，另一端看到後下載。檔案存在電腦端；限制由後端強制執行。

## 常數（後端定義，前端從 `GET /api/files` 讀取，不要寫死）
| 名稱 | 值 |
|---|---|
| 單檔上限 `max_file_bytes` | 200 MB（209,715,200）|
| 總量上限 `max_total_bytes` | 2 GB（2,147,483,648）|
| 檔案數上限 `max_files` | 200 |
| 保留時間 `ttl_seconds` | 86,400（24 小時，從上傳完成起算）|
| 同時上傳數上限 | 4 |

## 驗證與安全（所有 `/api/files*` 都適用）
- 需要已配對身分：電腦端或已配對手機（與 `/api/text` 相同的 Cookie 規則）。未配對一律 401。
- 會改動資料的請求（上傳、刪除）必須：`Origin` 等於 `http://<Host標頭>`；否則 403。
- 一律先驗證身分、Origin、Content-Length，**通過後才開始讀 body**。
- 下載一律回 `Content-Type: application/octet-stream`＋`Content-Disposition: attachment`（不得讓瀏覽器直接顯示或執行檔案內容）；沿用既有的 `X-Content-Type-Options: nosniff` 等標頭。
- 檔名消毒：只取最後一段（去掉任何路徑）、去掉控制字元與 `<>:"/\|?*`、去掉結尾的空白與點、Windows 保留名（CON、PRN、AUX、NUL、COM1–9、LPT1–9）前面加底線、長度上限 120 個字元（保留副檔名）；消毒後為空則用 `file`。磁碟上以 `<id>.bin` 儲存，**不用使用者提供的檔名當路徑**。
- 錯誤回應一律 JSON：`{"error":"<繁中訊息>","code":"<代碼>"}`。

## 端點
### `GET /api/files`
回 200：
```json
{
  "files": [
    {"id":"0123456789abcdef","name":"照片.jpg","size":123456,"created_at":1793900000,
     "expires_at":1793986400,"source":"phone"}
  ],
  "used_bytes": 123456,
  "max_file_bytes": 209715200,
  "max_total_bytes": 2147483648,
  "max_files": 200,
  "ttl_seconds": 86400,
  "revision": 7
}
```
- `files` 依 `created_at` 由新到舊；`source` 為 `"desktop"` 或 `"phone"`（由伺服器依身分決定，不信任客戶端）。
- `id`：16 個小寫十六進位字元。時間為 Unix 秒。已過期的不得出現。

### `POST /api/files?name=<百分比編碼的 UTF-8 檔名>`
- 標頭：`Content-Type: application/octet-stream`、`Content-Length`（必填，不支援 chunked）。body 為原始檔案位元組。
- 成功 200：`{"file":{...同上單筆...},"revision":8}`
- 失敗：

| 狀態 | code | 情況 |
|---|---|---|
| 400 | `bad_request` | 缺 `name`、缺 Content-Length、Content-Type 不對、body 比宣告的短 |
| 401 | `not_paired` | 未配對 |
| 403 | `origin_rejected` | Origin 不符 |
| 413 | `file_too_large` | 超過單檔上限（看到 Content-Length 就立刻拒絕，不讀 body）|
| 507 | `quota_exceeded` | 加上這個檔案會超過總量上限 |
| 409 | `too_many_files` | 已達檔案數上限 |
| 503 | `busy` | 同時上傳已達 4 個 |

- 串流寫入磁碟（先寫 `<id>.part`，完成後原子改名成 `<id>.bin`）；**不得把整個檔案讀進記憶體**。
- 讀取 body 用「閒置逾時」（連續 30 秒沒收到資料就中止），不使用整體 15 秒期限。失敗或中斷時刪掉 `.part`。
- 不得因為上傳而讓其他請求（文字同步、事件串流）卡住。

### `GET /api/files/<id>`（也支援 `HEAD`）
- 200：串流送出，`Content-Length` 正確，`Content-Disposition: attachment; filename="<ASCII 備用名>"; filename*=UTF-8''<百分比編碼的完整名稱>`。
- `<id>` 格式不符、不存在或已過期：404。

### `POST /api/files/delete`
- body：`{"id":"<16 hex>"}`，Content-Type 為 `application/json`。成功 200 `{"ok":true,"revision":N}`；找不到 404。手機與電腦都可以刪。

## 同步通知
- `revision` 在檔案清單每次變動（新增、刪除、過期清除）時加 1。
- 現有的事件串流（`/api/events`）與 `GET /api/state` 的回應各多一個欄位 `"files_revision": <數字>`。前端發現數字變了就重新抓 `GET /api/files`；前端另外每 10 秒輪詢一次當備援。

## 清除規則（後端）
- 啟動時：刪掉 `.part` 殘留、已過期檔案、沒有對應索引的孤兒檔，並與索引對帳。
- 執行中：每 60 秒掃一次過期；`GET /api/files` 時也順手清。
- 儲存位置：資料目錄下的 `files/`（資料目錄就是既有 `data_dir()`，即 `%APPDATA%\FastInput`）；索引用 `files/index.json`（原子寫入）。
