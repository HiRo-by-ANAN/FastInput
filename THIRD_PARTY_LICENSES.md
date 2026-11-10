# 第三方軟體 / Third-party software / サードパーティ

FastInput 使用下列開源專案。每個相依套件仍適用其原有授權；實際解析的版本與間接相依套件記錄於 `Cargo.lock`。發行版另附自動產生的 `THIRD_PARTY_NOTICES.txt`，收錄 Windows 建置相依樹中每個 crate 隨附的原始授權文字（由 `packaging/windows/generate-third-party-notices.ps1` 產生）。

## Rust（內網服務）

| 專案 | 授權 |
| --- | --- |
| if-addrs | MIT OR BSD-3-Clause |
| png（及 fdeflate、flate2、miniz_oxide、crc32fast、adler2、simd-adler32、bitflags） | MIT OR Apache-2.0（miniz_oxide 另可選 Zlib／0BSD；simd-adler32 為 MIT） |
| qrcode | MIT OR Apache-2.0 |
| rand（及 rand_core、rand_chacha、ppv-lite86、getrandom、zerocopy） | MIT OR Apache-2.0（zerocopy 另可選 BSD-2-Clause） |
| serde、serde_core、serde_derive、serde_json（及 itoa、memchr、zmij） | MIT OR Apache-2.0（memchr：Unlicense OR MIT；zmij：MIT） |
| proc-macro2、quote、syn、unicode-ident（建置相依） | MIT OR Apache-2.0（unicode-ident 另含 Unicode-3.0） |
| windows-sys、windows-link | MIT OR Apache-2.0 |
| cfg-if | MIT OR Apache-2.0 |
| zlib-rs | Zlib |

## .NET（Windows 外殼）

| 專案 | 授權 |
| --- | --- |
| .NET / Windows Desktop Runtime 8（隨發行版內含） | MIT（Microsoft、.NET Foundation） |
| Microsoft.Web.WebView2 SDK 1.0.4314.35 | BSD-3-Clause 風格（Microsoft） |

Microsoft Edge WebView2 Runtime 為 Windows 內建／Evergreen 元件，不隨本專案散布，適用 Microsoft 自身條款。Windows 版使用作業系統提供的字型，不隨程式散布字型檔。

## 建置工具

| 專案 | 授權 |
| --- | --- |
| WiX Toolset 5.0.2（產生 MSI） | MS-RL |

WiX 為安裝程式建置工具，不併入發行版；其授權不影響 FastInput 的 GPL-3.0-or-later。

## 授權相容性

上述執行期相依套件皆為寬鬆授權（MIT、Apache-2.0、BSD、Zlib、Unlicense、0BSD、Unicode-3.0），可與 GPL-3.0-or-later 的 FastInput 合併散布。這份說明不會取代或修改任何上游授權條款。
