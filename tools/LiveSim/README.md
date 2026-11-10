# LiveSim：M2b／M4 真實桌面驗證工具

此專案不依賴外部 NuGet，連結正式的 C# controller、Win32/UIA 轉接層與安全探針。建置本身不開 GUI、不安裝 hook、不送輸入。**這輪只編譯，尚未執行以下桌面情境，沒有桌面實測數字。**

執行 `fixture`、`chrome`、`enter`、`all` 會開啟自有視窗、註冊 Ctrl+Alt+F12、安裝全域 hook、用 SendInput 點選自有欄位；測試期間請讓桌面保持解鎖，避免操作滑鼠鍵盤。真實操作會觸發接手保護而使情境失敗。緊急停止可按 Ctrl+Alt+F12。請使用一般使用者權限；高權限宿主會被正式安全探針拒絕。

## 建置

在專案根目錄 PowerShell 執行：

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet restore tools\LiveSim --source "$env:USERPROFILE\.nuget\packages"
dotnet build tools\LiveSim --no-restore
```

準備正式 Rust 後端（本工具不下載、不建置 Rust）：

```powershell
$env:RUSTUP_TOOLCHAIN = 'stable-x86_64-pc-windows-msvc'
cargo build --offline --target-dir .\target-p10
```

若已有其他位置的後端，所有執行指令可加 `--server C:\absolute\path\fastinput.exe`，或設定 `FASTINPUT_TEST_SERVER`。預設使用 `target-p10\debug\fastinput.exe`。

## 執行各情境

```powershell
# 完整情境（包含 Chrome；找不到瀏覽器會明確 FAIL）
dotnet run --project tools\LiveSim --no-build -- all

# 只測純政策假資料，不開終端機、不送輸入
dotnet run --project tools\LiveSim --no-build -- blacklist

# WinForms：全部或單項
dotnet run --project tools\LiveSim --no-build -- fixture
dotnet run --project tools\LiveSim --no-build -- fixture --case middle
dotnet run --project tools\LiveSim --no-build -- fixture --case incremental
dotnet run --project tools\LiveSim --no-build -- fixture --case long1000
dotnet run --project tools\LiveSim --no-build -- fixture --case reconnect
dotnet run --project tools\LiveSim --no-build -- fixture --case disconnect
dotnet run --project tools\LiveSim --no-build -- fixture --case foreground
dotnet run --project tools\LiveSim --no-build -- fixture --case password
dotnet run --project tools\LiveSim --no-build -- fixture --case multi
dotnet run --project tools\LiveSim --no-build -- fixture --case rich

# 使用正式 RealTargetPicker，依提示親手點欄位；不送測試滑鼠事件
dotnet run --project tools\LiveSim --no-build -- fixture --case middle --physical-clicks
dotnet run --project tools\LiveSim --no-build -- fixture --case password --physical-clicks

# Chrome/Edge：全部或單欄
$env:PROBELAB_BROWSER = 'chrome'
dotnet run --project tools\LiveSim --no-build -- chrome
dotnet run --project tools\LiveSim --no-build -- chrome --case input
dotnet run --project tools\LiveSim --no-build -- chrome --case textarea
dotnet run --project tools\LiveSim --no-build -- chrome --case editable
dotnet run --project tools\LiveSim --no-build -- chrome --case password
dotnet run --project tools\LiveSim --no-build -- chrome --case input --physical-clicks
dotnet run --project tools\LiveSim --no-build -- chrome --case textarea --physical-clicks

# 在每筆手機事件與 POST 之間加入延遲；延遲會計入延遲指標
dotnet run --project tools\LiveSim --no-build -- fixture --case middle --delay-ms 120
```

可設定 `LIVESIM_BROWSER_PATH` 指定 Chrome/Edge exe。偵測路徑沿用 ProbeLab 的 Program Files、Program Files (x86)、LocalAppData；`PROBELAB_BROWSER=chrome` 會排除 Edge。隔離 browser 使用每次全新 user-data-dir、loopback CDP port、本機 file fixture、禁止外連的 proxy/resolver 參數。CDP 僅讀回欄位、頁面事件、驗證 profile，清理時對已確認的 profile 呼叫 Browser.close；不使用 CDP 聚焦、點擊、改 selection 或寫文字。

|情境|預期|
|---|---|
|middle、multi、rich|先 Goagle，再 Google；第二次轉接層操作只刪 1、插 1；最終全文相符|
|incremental|G → Go → Google；不清空欄位|
|long1000|1,000 個 x；全文相符|
|reconnect|重建 HTTP 連線，以 status 對帳、重送同 seq；不新增套用|
|disconnect|停止 heartbeat 超過 3 秒；舊 epoch 的後續文字被拒絕|
|foreground|切換到自有 test host 視窗；epoch 撤銷，不寫 decoy|
|password|安全探針拒絕，轉接層 apply 0 次|
|blacklist|WindowsTerminal/conhost/cmd/powershell/pwsh/wt/OpenConsole 假程序資料全部拒絕；沒有啟動真終端機|
|chrome input/textarea/editable|Goagle → Google，UIA 選範圍＋Unicode 寫入，CDP 獨立讀回相符|
|chrome password|拒絕綁定，轉接層 apply 0 次|

每個正常情境亦送出重複 seq、缺號 seq（預期 HTTP 409）、停止後的新 seq（預期拒絕），並觀察停止後 200 ms 沒有新增轉接層操作。若桌面被覆蓋或前景不符，hit-test 失敗時不送點擊。

## M4：虛擬 Enter（只編譯，未執行）

以下指令供使用者在允許真實桌面互動的環境執行；本輪沒有啟動 LiveSim、fixture、GUI、瀏覽器或任何輸入／hook。`enter` 群組會開啟一個有單行 TextBox、密碼欄位、decoy 與預設按鈕的自有表單，採正式 Rust HTTP 與 C# 執行鏈。它只連線至 `127.0.0.1`。

```powershell
# 全部 M4 情境；預設後端 target-p10\debug\fastinput.exe
dotnet run --project tools\LiveSim --no-build -- enter

# 分別執行
dotnet run --project tools\LiveSim --no-build -- enter --case disabled
dotnet run --project tools\LiveSim --no-build -- enter --case enabled
dotnet run --project tools\LiveSim --no-build -- enter --case blacklist

# 正式 picker：依提示親手點欄位，取代測試 marker 的滑鼠點擊
dotnet run --project tools\LiveSim --no-build -- enter --physical-clicks
```

|情境|檢查|
|---|---|
|disabled|宿主保留預設 `VirtualEnterEnabled=false`；先套用文字、模擬完整 640 ms 長按後僅 POST 一次 `/api/realtime/enter`；必須 409 `enter_disabled`，Enter 對話鍵事件、預設按鈕動作、Enter adapter 呼叫皆為 0|
|enabled|僅由本機測試宿主 stdin `enter-on` 打開桌面設定，HTTP 無設定入口；文字 ACK 後模擬 640 ms 長按；Enter 對話鍵事件、預設按鈕動作、Enter adapter 呼叫各恰好 1；同 `enter_id` 重送不得多送；回報 `PAUSED/enter_sent`，之後文字被拒且 300 ms 觀察期新增 adapter edit 為 0|
|blacklist|桌面設定打開仍不能綁定密碼 fixture；偽造 epoch 的 Enter HTTP 意圖亦被拒；Enter 對話鍵事件、預設按鈕動作、Enter adapter 呼叫各為 0，password／decoy 不變|

JSON 每個情境新增 `EnterEvents`、`DefaultButtonClicks`、`EnterAttempts`、`PostEnterApplications`，未完成量測為 null。`EnterEvents` 是 fixture `ProcessDialogKey` 觀察的無修飾 Enter，`DefaultButtonClicks` 是預設按鈕收到的動作，`EnterAttempts` 是正式 Enter adapter 呼叫完成次數。後兩者與 fixture 事件共同驗證一次執行；不能把 adapter 呼叫數單獨當作 OS 輸入成功。Enter 完成後不透過正式 controller 讀回欄位，fixture 的獨立快照僅供本工具觀察副作用，無法證明任意應用送出動作成功。

黑名單 fixture 驗證的是**綁定前拒絕密碼欄位**，無有效 epoch，因此不宣稱已驗證「綁定後突然變為黑名單／高權限」的桌面競態；終端機程序黑名單另有原本純政策假資料 `blacklist` 群組。一般使用者程序無法安全模擬所有高權限／安全桌面情境；仍未驗證。`enter-on` 是測試宿主內部的明確偏離，會省略正式主視窗的風險確認；正式 UI 的預設關閉、確認文案、拒絕確認、設定保存與關閉瞬間撤銷須人工另驗。手機進度環、放開取消與不能由鍵盤／SSE／heartbeat／status 觸發，也須由手機網頁驗證，本工具只模擬已完成的一次長按意圖。焦點切換與最終 `SendInput` 間競態、修飾鍵被硬體同時按下、逾時／部分送出的 OS 行為均仍「不確定」，不能以本輪只編譯宣稱通過。

## 自動核准與測試點擊的邊界

程式架構為 orchestrator 啟動兩個**兄弟程序**：TEST HOST（真 controller／safety／adapter／Rust 子程序）及 fixture。fixture 不是 TEST HOST 子程序，因此不需要繞過正式的自身與子程序黑名單。

`--host`、`--fixture` 是本專案內部入口，必須具有 `FASTINPUT_LIVESIM=1`；orchestrator 只對自己的子程序設定。自動核准類別只編譯在 tools/LiveSim，正式 FastInput 外殼不存在此入口。不要把此環境變數當成正式授權功能。

正式 picker 拒絕所有 injected 點擊；SendInput 無法製造「沒有 LLMHF_INJECTED」的事件。為使自動情境可跑，**測試宿主專用 picker** 僅接受 LiveSim 測試 marker 的 SendInput 點擊，延後 90 ms 讓焦點成立，再呼叫正式 `WindowsTarget.Capture`。此 marker 與正式轉接層自注入 marker 不同；選定後同樣由正式實體輸入監測器處理，任何非正式 marker 輸入都視為接手。

未指定 `--physical-clicks` 時，這是測試入口的明確偏離，不能以此證明正式 picker 對硬體點擊的表現。

指定 `--physical-clicks` 後，宿主使用正式 `RealTargetPicker`，不用 TestPicker；orchestrator 完成核准及 claim，才在主控台提示應點選的欄位及約略座標。請在 30 秒內用實體滑鼠點欄位，之後等待文字驗證完成。整個群組會在每項情境重新提示；自動核准仍只存在測試宿主。此模式不呼叫 `Native.Click`，能驗證正式 picker 對硬體點擊的處理；JSON 記錄 `physical_clicks=true`。只執行 blacklist 時 `desktop_executed=false`。

正式實體輸入監測器亦用於兩種模式。可先執行 `fixture --case middle --physical-clicks`，完成目標點選後再親手打字或點別處，預期撤銷／暫停，該次正常文字情境會明確 FAIL，不能將這個刻意接手的 run 當成文字正確性通過。`fixture --case foreground` 則自動切到自有宿主視窗，檢查撤銷。1 秒 callback 後存活、overlay 不搶焦點、DPI/多螢幕、鎖定、安全桌面仍須人工在正式外殼驗證；工具不刻意阻塞 hook callback。

emoji 的 UIA 字元偏移仍「不確定」。上面所有 LiveSim 文字情境都是 BMP，沒有 emoji 驗證指令。人工檢查時請在正式外殼核准並真實點欄位，再從手機依序輸入 `A`、尾端追加 `😀`，預期 `A😀`；之後嘗試在 S 中間修改，預期暫停。另在 fixture 預先輸入含 emoji 的 P/Q 後重新接管，預期拒絕。這些需真實手機／正式外殼配合，不能用 BMP LiveSim PASS 代替。

網址列為可選項，這版不自動操作網址列。人工核准正式外殼後，真實點擊隔離 Chrome 網址列，將游標收合，再由手機送文字；不得按 Enter。原網址是 P/Q 的一部分，預期只在游標處接管，不整欄清空。可在 `BrowserFixture` 或 ProbeLab 的 Omnibox UIA 讀回程式上擴充獨立情境；目前報告不把網址列列為已通過。

## 報告與量測定義

預設寫入 `tools/LiveSim/reports/latest.json`、`latest.md`，可加 `--output C:\absolute\report-dir`。退出碼 0 表示選取情境全部 PASS，1 表示至少一項失敗。每次執行會覆寫 latest，請保留需要比較的報告。

- 最終文字正確率：實際完成最終讀回的文字情境相符比例。未讀回為 null、不列分母，請同時檢查情境 FAIL 與樣本數；密碼和純政策測試不列分母。例外若發生在量測完成後，保留已取得的數字。
- 閃爍：fixture/DOM TextChanged/input 事件中，非空文字被清空，稍後原樣出現的次數。這是事件層指標，沒有以螢幕影格量測視覺閃爍。
- 錯目標寫入：自有 decoy／password 欄位有非預期變動的情境數；密碼拒絕情境也會從 fixture/CDP 實際讀回空值及 decoy，才記 0，沒有完成讀回則記 null。**不代表觀察了整個桌面的所有視窗**。
- 重複套用：重送同 seq 後新增的 adapter apply 次數。
- 單字替換多餘編輯：Goagle→Google 預期兩次總 apply，第二次應刪 1 插 1；超過的次數和字數計入。
- 停止 p95：orchestrator 送 host stdin stop 到 HTTP 權威狀態確認撤銷的時間，之後觀察 200 ms 零新操作；已因斷線／切換前景撤銷的情境不計停止樣本。這包含 IPC 與 HTTP 輪詢，不是原子 OS 保證。
- 手機事件→讀回核對延遲 p50/p95：開始一筆手機事件至正式協定回報 applied_seq + verified_readback；包含可選人工延遲、HTTP 與輪詢，不是僅 SendInput 耗時。

未量測數值為 JSON null；報告列樣本數。少量情境的 p95 不足以代表長時間壓力分布。方案目標為全文正確率 100%、其餘錯誤計數 0、停止 p95 <100 ms、延遲 p50 <100/p95 <250 ms；工具如實輸出，不會改寫失敗值使它達標。UIA emoji 字元單位仍「不確定」，這些 BMP 情境不能驗證 emoji 中間偏移。

## 清理

正常結束或可處理例外會依序關閉自己的隔離 browser、fixture、TEST HOST 與 Rust 子程序；3 秒內未退出的自有程序會被結束。每次 work-隨機值 資料夾在確認屬於指定 output 後刪除，包含臨時配對資料、Chrome profile、fixture 全文事件；只留下報告。工具不掃描或關閉既有 Chrome、既有 FastInput 或使用者視窗。

若強制終止 orchestrator、斷電或作業系統中斷，finally 無法保證執行。請只關閉標題帶 `LiveSim TEST HOST`、`FastInput LiveSim fixture`、`FastInput LiveSim <隨機值>` 的本次視窗。必要時在工作管理員核對這次 LiveSim／fastinput 子程序後結束；不要以名稱批次結束所有 Chrome 或 FastInput。確認本次程序全數退出，再刪除報告目錄下殘留的 `work-<隨機值>`。不要刪除使用者一般 Chrome profile。

