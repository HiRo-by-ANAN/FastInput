# M4 虛擬 Enter 規格（Rust／C#／手機三方共同依據）

## 原則（維護者的決定）
- 做，但 **預設關閉、預設不顯示**；只能在**電腦端設定**打開（`virtual_enter_enabled`，M2a 已有，預設 false）。手機、網頁、任何 HTTP 請求都不能打開它。
- 理由：按 Enter 是「做決定」的時刻，不該預設給捷徑。長按防誤觸；只送單一 Enter，**不提供 Tab、快捷鍵、其他按鍵、滑鼠**。
- C# 是最終權威：Rust 與手機的任何欄位都只是「意圖」。C# 每次都獨立檢查設定、目標、黑名單、序號。

## 流程
1. C# 在 `hello` 之後與設定變更時，送 `capabilities{virtual_enter:bool}`。為 `true` 才代表電腦端允許。
2. C# 在 `target_bound` 增加選用欄位 `app_label`：目標程式簡稱（≤32 字元、僅字母數字、空白、`.`、`-`、`_`、中日韓文字；不得含視窗標題、URL、路徑）。Rust 存入狀態快照給**同一憑證**的手機看（欄位 `target_app`）。
3. 狀態快照新增 `enter_available`（布林）＝ `capabilities.virtual_enter` 且 phase 為 ACTIVE 且目標未被 C# 標為禁用（`target_bound` 增加選用布林 `enter_allowed`，預設 false；終端機、高權限、密碼欄位、不支援者為 false）。
4. 手機：只有 `enter_available === true`、本機佇列為空、`accepted_seq === applied_seq ===` 本機最後序號時，才顯示「長按送出 Enter」按鈕（600 ms 長按，放開即取消；進度環；按下期間顯示「將送出 Enter 到：{target_app}」與自己文字尾端最多 20 字，皆用 textContent）。鍵盤 Enter（含手機鍵盤的 Enter／換行）**絕不**觸發；該按鈕不可用鍵盤啟動。
5. `POST /api/realtime/enter`（需 `X-FI-Realtime`，其餘共通規則同協定 §2）：
   ```json
   {"v":1,"request_id":"<id>","target_epoch":"<epoch>","enter_id":"<32 hex，手機產生>","after_seq":12,"hold_ms":640}
   ```
   Rust 檢查：phase ACTIVE；epoch 相符；`enter_available`；`after_seq === accepted_seq === applied_seq` 且無在途／待處理項目（否則 409 `text_pending`）；`hold_ms ≥ 600`（否則 422 `invalid_hold`）；一個 enter 在途最多一個（409 `busy`）；全 epoch 速率 ≤1 次／秒（429 `rate_limited`）；`enter_id` 同 id 同內容重送→回既有狀態不重複執行；同 id 不同內容→409 `sequence_conflict`。不符 `enter_available`：409 `enter_disabled`。成功回狀態快照加 `enter:{enter_id,status:"requested"}`。
6. Rust→C#：`{"type":"press_enter","request_id","target_epoch","enter_id","after_seq"}`（唯一新增、仍然沒有按鍵碼／HWND／座標）。
7. C#（最終）：檢查 `virtual_enter_enabled`（此刻重讀設定）、授權／租約／epoch／停止旗標、目標仍有效、前景＝目標、黑名單與完整性、`after_seq` 等於目前已套用序號且無待套用、無實體輸入暫停中；通過才以 `SendInput` 送**單一** `VK_RETURN` 按下＋放開（無修飾鍵，`dwExtraInfo` 帶我方標記）。送出後**不讀回、不宣稱欄位變化**；回 `enter_result`。
8. C#→Rust：`{"type":"enter_result","request_id","target_epoch","enter_id","status":"applied|rejected|unknown","reason":null|<白名單>}`。`applied` 僅代表「Enter 事件已送出且送出時目標仍是目標」。逾時／不確定→`unknown`（不重送）。
9. Enter 之後**一律結束本 epoch**：phase 轉 `PAUSED`，reason `enter_sent`（新增到 reason 白名單）；手機將已送文字鎖定並提示「已送出 Enter，請在電腦上重新點選欄位才能繼續」。理由：Enter 會讓目標內容與前後文改變，本系統不猜測、不續接。
10. 稽核：記 `enter` 事件（時間、匿名 session、程式簡稱、結果、reason），不記文字。
11. 新增 reason 白名單項目：`enter_sent`、`enter_disabled`、`enter_blocked_target`、`text_pending`、`enter_busy`。
12. 禁止事項：任何其他按鍵、連按、自動重複、長按重複觸發、由 SSE／heartbeat／status 觸發送 Enter、Enter 作為文字內容（文字內的換行仍被拒絕）。
