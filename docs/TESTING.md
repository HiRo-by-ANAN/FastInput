# 測試清單

自動化測試與需要真實桌面／真手機的檢查項目。

## 自動化（不需要桌面）

```powershell
cargo test --locked                                   # Rust：內網服務、實時核心、檔案、協定（63 項）
dotnet run --project tests/RealtimeTests              # C# 原生端：框架、安全、轉接層、Enter、多語系（112 項）
node tests/i18n-check.mjs                             # 三語字典一致性、無殘留硬編碼文字
node tests/i18n-dom.mjs                               # 網頁語言切換（DOM stub）
node tests/integration.mjs                            # 內網服務整合（需先 cargo build，必要時設 FASTINPUT_BINARY）
```

## 真實桌面（`tools/LiveSim`，執行期間請勿操作滑鼠鍵盤）

```powershell
cargo build --target-dir target-livesim
dotnet build tools/LiveSim
dotnet run --project tools/LiveSim --no-build -- all
dotnet run --project tools/LiveSim --no-build -- fixture --case middle --physical-clicks   # 需要親手點選欄位
```

涵蓋：Goagle→Google 的一刪一插、逐字不清空、1,000 字、重連、斷線撤權、密碼欄拒絕、換前景視窗撤權、多行／RichEdit、Chrome input／textarea／contenteditable、虛擬 Enter 開／關。報告輸出在 `tools/LiveSim/reports/`。

## 需要人工確認

- 核准視窗的焦點行為與 30 秒逾時；overlay 不搶焦點、位置在欄位外；多螢幕／混合 DPI。
- 緊急停止鍵實按；實體鍵盤／滑鼠打斷後授權收回。
- 主視窗底部「允許手機實時輸入」「允許手機虛擬 Enter」勾選框與確認視窗。
- 真手機（Chrome）：實時分頁、核對碼、英文逐字、中文詞組、語音整句、中間改字、檔案分頁、虛擬 Enter 長按 600 毫秒。
- 防火牆提示（私人網路）、WebView2 下載對話框。
- 尚未驗證：新版記事本（RichEditD2DPT）、WPF／Office／Electron、emoji 的 UIA 偏移、iPhone Safari、Edge、鎖定／睡眠／UAC。
