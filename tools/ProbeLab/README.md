# ProbeLab：FastInput M0 能力實驗工具

本工具收集 Windows 實際行為，供在真實桌面執行與判讀。編譯通過不代表桌面相容性通過。沒有 NuGet 套件；需要 .NET 8 SDK 與 Windows Desktop SDK。

## 編譯與純模型測試

從工作根目錄執行：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build tools/ProbeLab/ProbeLab.csproj -c Release
& 'C:\Program Files\dotnet\dotnet.exe' run --project tools/ProbeLab.Tests/ProbeLab.Tests.csproj -c Release
$probe = Join-Path $PWD 'tools\ProbeLab\bin\Release\net8.0-windows\ProbeLab.exe'
& $probe e4-unicode --model-only
& $probe report
```

`ProbeLab.Tests` 是離線主控台斷言專案，直接連結純模型來源，不參考 GUI 專案、不呼叫 Win32。涵蓋 UTF-16/scalar/grapheme、單區段最小 diff、重複字依 caret/selection 消除歧義、JSONL，以及 1000 組固定種子的 diff／窮舉最小成本比對。

`--model-only` **只允許** `e4-unicode`；其他子命令帶此選項會在建立任何視窗前拒絕。`report` 也不觸碰桌面。其餘實驗請勿在沙盒執行。

## 命令與建議順序

|命令|涵蓋實驗|用途與參數|
|---|---|---|
|`fixture-winforms --log <新檔>`|共用 fixture|另一行程啟動的五種控制項與 decoy；可加 `--commands <新檔>`、`--state <新檔>`。省略時使用 log 同資料夾的 `commands.jsonl`、`state.json`。|
|`e1-edit-replace`|E1、E7、E8|逐一測 single/multi/rich 的 SETSEL/REPLACESEL、UNDO、WM_SETTEXT；selection/文字競態、1200 ms UI 阻塞配 100 ms timeout、decoy 前景的指定 HWND 訊息。|
|`e4-unicode [--model-only]`|E4|模型分群；未指定 model-only 時另測 single/multi/rich 的每個 UTF-16 offset、Left/Right、Backspace/Delete。|
|`e5-sendinput [--trials 10]`|E5、E6、E14|single/multi 的英數、中文、surrogate pair、LF/CR/Tab，Shift+Left 中間替換與 decoy 競態；trials 為 1～100。|
|`e12-overlay`|E12|同 fixture PID 的不啟用置頂 overlay；點擊暫停／停止按鈕，比較前景、GUI 焦點、click 計數，各螢幕 DPI/工作區。|
|`e13-hooks`|E13、U16|鍵鼠 injected/marker、0/100/300/1000 ms callback 延遲與後續回呼、Ctrl+Alt+Shift+F9 註冊／衝突。|
|`e3-browser`|E3、瀏覽器 E5/E14|找 Edge/Chrome，以全新私有 profile 與隨機 49152～65535 loopback 高埠啟動；CDP DOM 真值和 UIA 逐欄比較，Unicode／中間修改／自動完成 Backspace。|
|`probe-notepad --file <尚不存在的暫存檔>`|E2|CreateNew 建立 0-byte 檔後啟動記事本，只做唯讀 UIA 探查。亦接受 `--probe-notepad <尚不存在的暫存檔>` 明確模式。|
|`report`|彙整|讀取 results JSON，寫出繁中 SUMMARY.md，缺少實驗列未執行。|

建議：模型 → E1 → E4 桌面 → E5 → E12 → E13 → E3 → E2 → report。每個命令可獨立重跑，會建立自己的 fixture。

```powershell
& $probe e1-edit-replace
& $probe e4-unicode
& $probe e5-sendinput --trials 10
& $probe e12-overlay
& $probe e13-hooks
# 瀏覽器需先自行停用 Wi-Fi、拔除網路，並停用啟用中的虛擬網路介面。
& $probe e3-browser
$notepadDir = Join-Path $env:TEMP ('ProbeLab-Notepad-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $notepadDir | Out-Null
# 不要預先建立 empty.txt；工具必須以 CreateNew 建立，確保是新檔。
& $probe probe-notepad --file (Join-Path $notepadDir 'empty.txt')
& $probe report
```

請使用一般權限、已登入且未鎖定的本機桌面。整段實驗期間不要碰鍵盤滑鼠、不要切換視窗、不要恢復網路連線；關閉會彈通知或覆蓋測試視窗的程式有助避免拒絕或污染落點。fixture 的 decoy 切換由工具完成。若 SetForegroundWindow 不被 Windows 允許，命令會記錄失敗並略過依賴該焦點的輸入，請查看報告再重跑。不要對使用者的記事本、現有瀏覽器或其他程式手動指定 HWND/PID：命令不提供這種選項。

## 強制安全邊界

- `OwnedTarget` 只接收此程式直接啟動並保留的 `Process`；每次視窗訊息／SendInput 前重新確認目標 HWND 的 PID 及前景 PID 都等於該行程。沒有「略過檢查」開關，無效目標會記錄安全拒絕。
- overlay 在 fixture 子行程內建立，與欄位、decoy 同 PID；滑鼠另做 `WindowFromPoint` 精確命中檢查。停止／暫停按鈕在 M0 只記 click，沒有實時輸入產品狀態機。
- **PID 檢查無法保證原欄位焦點，也無法把全域 SendInput 與檢查變成原子操作。** E5 特意只切到自己 decoy，統計實際落點；絕不以現有使用者視窗製造競態。真正的外部視窗切換，仍可能發生在檢查與系統處理事件之間，所以實驗期間必須保持桌面無人操作。
- 只清理自己建立的 fixture／私有瀏覽器行程；記事本一律保留供人工關閉，避免誤關使用者分頁。不提權、不用 uiAccess、不寫登錄、不安裝。
- 每次直接啟動外部程式前，stdout 會印完整執行檔與所有引數。
- 瀏覽器另**強制離線前置檢查**：有任何 Up 的非 loopback 網路介面便略過，不會啟動瀏覽器；每次輸入 guard 亦重新檢查。瀏覽器載入本機 file fixture，CSP 禁連線，CDP 設 offline/blocked URLs、關閉背景服務、代理指向不轉送的本機 CDP，CDP HTTP/WebSocket 明確不使用系統代理且只接受 `127.0.0.1`。本工具不修改防火牆或網卡；瀏覽器背景 native service 未必遵守所有命令列旗標，因此必須全程保持實體離線，不能把旗標視為 OS 網路隔離。
- 瀏覽器 HWND 若不屬於直接啟動 PID，便拒絕；不會依行程名稱放行既有瀏覽器，亦不猜測／放行任意 renderer PID。CDP 還須精確符合 fixture URL 與新 profile 的命令列；每次 Runtime.evaluate 均檢查頁面 URL。
- 記事本須指定系統 TEMP 下的新檔（父資料夾先存在、路徑不可含 reparse point）；CreateNew 拒絕任何既有檔案。僅探查新增頂層視窗的 class、ControlType、AutomationId、Patterns、密碼與可編輯旗標，不儲存視窗 Name 或使用者文字。新視窗也可能還原舊分頁，因此預設連 GetText/GetValue 都不呼叫。只有同啟動 PID、唯一新視窗、無分頁、唯一傳統 Edit、檔名符合且該 Edit 正有焦點時，才讀取並立即只保留長度；其他情況明記未執行。現代記事本若接到既有分頁，不掃描該既有視窗。**這個保守限制可能使 E2 全文／selection 量測無法取得。**
- hooks 不保存真實鍵值、scan code 或滑鼠座標，只保存裝置、事件類型、injected/marker、時間。fixture 的完整測試字串與鍵值可記錄，請不要在 fixture 內輸入私密文字。

## Fixture 控制檔與 JSON Lines

自動實驗會使用 `%TEMP%/ProbeLab-<GUID>/`，內含 `events.jsonl`、`commands.jsonl`、`state.json`。保留檔案便於人工核對；工具不遞迴刪除暫存資料夾。手動 fixture 範例：

```powershell
$fixtureDir = Join-Path $env:TEMP ('ProbeLab-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDir | Out-Null
& $probe fixture-winforms --log (Join-Path $fixtureDir 'events.jsonl')
```

log/state 不可已存在；command 檔必須不存在或為空檔，三者必須在同一個無 reparse point 的目錄。fixture 啟動後持有 command 檔 handle，不允許替換／刪除；只接受該檔，不接受任何外部 HWND/PID。另一個主控台可將下列 JSON 各寫一行到它的 commands 檔：

```jsonl
{"seq":1,"op":"focus","id":"single"}
{"seq":2,"op":"text","id":"single","text":"Goagle"}
{"seq":3,"op":"select","id":"single","start":2,"length":1}
{"seq":4,"op":"block","id":"single","ms":1200}
{"seq":5,"op":"snapshot"}
{"seq":6,"op":"close"}
```

id：single/multi/rich/password/readonly/decoy。seq 必須遞增；不完整行等待換行後才解析；一般指令在操作完成才 ack，block 在進入阻塞前先 flush log 與 ack（0～10000 ms）。`lastCommandError` 非 null 表示失敗。command 每 15 ms timer tick 最多處理一行，UI 阻塞期間不能處理其他 command。

`state.controls` 包含 HWND、full text、UTF-16 start/length、CanUndo、Focused 與螢幕矩形；`overlayClicks` 記錄按鈕計數。fixture JSONL 每行有 UTC、Stopwatch ticks/frequency、PID、event type、control、完整測試文字、selection 與 WM 參數。TextBox 沒有公開 SelectionChanged，所以同時在訊息完成後觀察 selection 與 15 ms polling；RichTextBox 用原生 SelectionChanged。WM before/after 與鍵事件可和主程序的 SendInput 回傳及 timeout 耗時交叉核對。

## 判準、結果與待驗證假設

所有實驗主報告寫 `tools/ProbeLab/results/<代號>.json` 及 `.md`，每步保留參數、Stopwatch timestamp、原始 state/events、判準與 `Pass=true/false/null`。null 表示觀察／不可取得／未判定，不能解讀為通過。重跑覆寫該代號的最近結果，需要保留歷史時請自行複製 results。`report` 列缺少實驗為未執行；部分步驟略過會標為部分執行。

E1 同次資料也輸出 E7/E8；E5 同次資料也輸出 E6/E14；E13 同次資料也輸出 U16，均記來源且保留完整原始資料。E3 的瀏覽器 E5/E14 量測保留於 E3，閱讀 SUMMARY 時須一起看。standalone fixture 不給實驗通過結論，其 JSONL 是後續實驗的佐證。

- E1：Goagle UTF-16 `[2,3)` 換一個 o，應為 Google；CanUndo=true 且一次 EM_UNDO 回 Goagle；WM_SETTEXT 的 selection/undo 為對照觀察。E7 的讀回偏離偵測以 fixture 真值驗證，不能當成外部程式原子交易證明。decoy 前景時只改指定原欄位，decoy 字串應不變。
- E8：1200 ms UI 阻塞已開始後，以 100 ms SendMessageTimeout 量測是否逾時，再等 1400 ms 讀回是否晚完成；永不重送不確定寫入。傳字串的 unmanaged buffer 保留到 fixture 結束，避免假設 timeout 已取消。
- E4：StringInfo 的分群只代表此 .NET/runtime；Win32 Edit/RichEdit 的左右鍵／刪除可能依 UTF-16、scalar、grapheme 或其他規則處理，必須看每步差量。每一 UTF-16 offset（包含 surrogate/ZWJ 內部）都記 requested/actual selection。
- E5/E6/E14：一般文字須完整相等，SendInput 回傳須等於事件數；LF/CR/Tab 只看 fixture single/multi 事件和最終文字，不推論其他 app 的送出行為。😀 以兩 surrogate 單位各送 down/up，共 4 events；控制字元是 KEYEVENTF_UNICODE，並非 VK_RETURN/VK_TAB。Shift+Left 從 offset 3 選 a 後送 o 應得 Google。
- E3：DOM 是 fixture 真值，UIA 須列可取得的 Value/Text/selection 與相等比較；不支援列 null。密碼欄 UIA 不讀值，DOM 只記 fixture 字串。React 欄是無套件的受控欄位模擬，不代表真 React 測試。大寫截斷採 JS UTF-16 slice(0,10)。網址列式欄位為頁內模擬，並非真瀏覽器網址列；goo 補成 google.com 並選 `[3,10)`，一次 Backspace 應剩 goo。
- E12：需 SendInput 成功、GetGUIThreadInfo 前後可讀、原焦點為 fixture single、前景與焦點 HWND 不變，而且該按鈕計數只加一。不同 DPI 下的座標與 WinForms 子按鈕是否搶焦點必須實測；不是先宣稱 NOACTIVATE 一定足夠。
- E13/U16：自有 marker 應有 injected 旗標。每個 delay phase 重新裝鍵盤 hook；後續 marker 未見僅表示可能遭移除，也可能是事件路由／訊息迴圈失敗。工具不讀 LowLevelHooksTimeout 登錄值，不把 1000 ms 當成這台主機固定閾值。熱鍵若已被其他程式佔用，記未能進行自我衝突測試；不搶占。

無瀏覽器／無法證明私有 PID／前景不符／記事本新分頁不可識別，都會保留原因。exit code：0 完成且沒有 false 判準（仍可能包含略過），1 有判準失敗，2 參數或執行例外。請以 JSON 的逐項狀態與原始值判讀，不能僅看 exit code。
