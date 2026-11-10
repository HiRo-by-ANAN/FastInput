# 參與 FastInput 開發

感謝你協助改善 FastInput。

## 提交修改前

- 建立新 Issue 前，請先搜尋是否已有相同問題。
- 請勿加入雲端中繼、強制帳號、遙測或任何會把使用者輸入文字送出區域網路的行為。
- 實時輸入（Windows）的安全規則不得放寬：每次需電腦端核准、租約期限、實體輸入收回、黑名單、稽核紀錄不含文字、虛擬 Enter 預設關閉。若要調整，請先建立 Issue 說明理由與風險。
- 絕對不要提交裝置名稱、IP 位址、本機路徑、配對碼、`state.json`、稽核紀錄、簽章檔案，或含有個人資料的截圖與測試文件。

涉及協定或使用者介面的大範圍修改，請先建立 Issue 討論。

## 建置與測試

Windows 需要 Rust stable、.NET 8 SDK 與 Visual Studio 的「使用 C++ 的桌面開發」工作負載。

```powershell
cargo test --locked
dotnet build native/windows/FastInput.Windows.csproj
dotnet run --project tests/RealtimeTests
powershell -ExecutionPolicy Bypass -File scripts/build-windows.ps1
```

`tools/LiveSim` 是需要真實桌面的測試（會開啟視窗並模擬輸入），執行期間請不要操作滑鼠鍵盤：

```powershell
dotnet run --project tools/LiveSim -- all
```

新增或修改介面文字時，請同步更新三種語言（見 [docs/I18N.md](docs/I18N.md)）。

## Pull Request

請說明具體問題、修改後的行為與測試方式。提交貢獻即表示你同意以 GPL-3.0-or-later 授權該項貢獻。
