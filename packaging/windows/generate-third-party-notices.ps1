# 產生 THIRD_PARTY_NOTICES.txt：收錄 Windows 建置相依樹中每個 Rust crate、WebView2 SDK 與 .NET 的授權文字。
# 用法：powershell -ExecutionPolicy Bypass -File packaging/windows/generate-third-party-notices.ps1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $root 'THIRD_PARTY_NOTICES.txt'
$env:RUSTUP_TOOLCHAIN = if ($env:RUSTUP_TOOLCHAIN) { $env:RUSTUP_TOOLCHAIN } else { 'stable-x86_64-pc-windows-msvc' }
$registry = Get-ChildItem (Join-Path $env:USERPROFILE '.cargo\registry\src') -Directory | Select-Object -First 1
Push-Location $root
try {
    $tree = & cargo tree --target x86_64-pc-windows-msvc --locked -e normal,build --prefix none --format '{p}' 2>$null
} finally { Pop-Location }
$crates = $tree | ForEach-Object { if ($_ -match '^(\S+) v(\S+)') { "$($Matches[1]) $($Matches[2])" } } |
    Where-Object { $_ -notmatch '^fastinput ' } | Sort-Object -Unique
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('FastInput 第三方軟體授權文字 / Third-party notices / サードパーティ通知')
[void]$sb.AppendLine('FastInput itself is licensed under GPL-3.0-or-later (see LICENSE).')
[void]$sb.AppendLine('每個相依套件仍適用其原有授權。以下為 Windows 建置相依樹內各 Rust crate 隨附的原始授權文字。')
[void]$sb.AppendLine(''.PadRight(78, '='))
foreach ($c in $crates) {
    $name, $ver = $c -split ' '
    $dir = Join-Path $registry.FullName "$name-$ver"
    if (-not (Test-Path $dir)) { throw "找不到 $name $ver 的原始碼（請先 cargo fetch）。" }
    $license = (Select-String -Path (Join-Path $dir 'Cargo.toml') -Pattern '^license\s*=\s*"(.*)"' | Select-Object -First 1).Matches.Groups[1].Value
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("--- $name $ver ($license) ---")
    $files = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|UNLICENSE|license-|NOTICE)' } | Sort-Object Name
    if (-not $files) { throw "$name $ver 沒有授權檔。" }
    foreach ($f in $files) {
        [void]$sb.AppendLine("[$($f.Name)]")
        [void]$sb.AppendLine((Get-Content $f.FullName -Raw))
    }
}
$wv = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2') -Directory | Sort-Object Name | Select-Object -Last 1
[void]$sb.AppendLine(''.PadRight(78, '='))
[void]$sb.AppendLine("--- Microsoft.Web.WebView2 SDK $($wv.Name) (BSD-3-Clause style, Microsoft) ---")
[void]$sb.AppendLine((Get-Content (Join-Path $wv.FullName 'LICENSE.txt') -Raw))
$notice = Join-Path $wv.FullName 'NOTICE.txt'
if (Test-Path $notice) { [void]$sb.AppendLine((Get-Content $notice -Raw)) }
$dotnet = Get-Item 'C:\Program Files\dotnet'
$dnVer = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory | Sort-Object Name | Select-Object -Last 1).Name
[void]$sb.AppendLine(''.PadRight(78, '='))
[void]$sb.AppendLine("--- .NET / Windows Desktop Runtime $dnVer (MIT, Microsoft / .NET Foundation) ---")
[void]$sb.AppendLine((Get-Content (Join-Path $dotnet.FullName 'LICENSE.txt') -Raw))
$tp = Join-Path $dotnet.FullName 'ThirdPartyNotices.txt'
if (Test-Path $tp) { [void]$sb.AppendLine((Get-Content $tp -Raw)) }
[void]$sb.AppendLine(''.PadRight(78, '='))
[void]$sb.AppendLine('Microsoft Edge WebView2 Runtime 為 Windows 內建／Evergreen 元件，不隨本專案散布；適用 Microsoft 自身條款。')
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
Write-Host "已產生 $out（$($crates.Count) 個 crate）"
