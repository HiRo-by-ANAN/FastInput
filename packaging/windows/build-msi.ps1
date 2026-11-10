# 建置 FastInput MSI。需要：WiX Toolset 5（dotnet tool install --global wix --version 5.0.2）
# 與 UI 延伸：wix extension add -g WixToolset.UI.wixext/5.0.2
# 用法：powershell -ExecutionPolicy Bypass -File packaging/windows/build-msi.ps1 [-Version 0.1.0]
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$source = Join-Path $root 'dist\FastInput-windows'
if (-not (Test-Path (Join-Path $source 'FastInput.exe'))) { throw '請先執行 scripts\build-windows.ps1 產生 dist\FastInput-windows。' }
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = (Get-Command wix -ErrorAction Stop).Source }
$work = Join-Path $root 'dist\.msi-work'
New-Item $work -ItemType Directory -Force | Out-Null

# 授權 RTF（顯示於安裝精靈授權頁）
$text = [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$esc = $text.Replace('\', '\').Replace('{', '\{').Replace('}', '\}') -replace "`r?`n", "\par`r`n"
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}\f0\fs18 $esc}"
$rtfPath = Join-Path $work 'license.rtf'
[IO.File]::WriteAllText($rtfPath, $rtf, [Text.Encoding]::ASCII)

$out = Join-Path $root "dist\FastInput-Windows-x64-$Version.msi"
& $wix build (Join-Path $PSScriptRoot 'FastInput.wxs') -arch x64 -culture zh-TW -ext WixToolset.UI.wixext `
    -d "ProductVersion=$Version" -d "SourceDir=$source" -d "RepoRoot=$root" -d "LicenseRtf=$rtfPath" `
    -d "IconFile=$(Join-Path $root 'native\windows\FastInput.ico')" -o $out
if ($LASTEXITCODE -ne 0) { throw 'wix build 失敗。' }
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
Set-Content -Path "$out.sha256" -Value "$hash *$(Split-Path $out -Leaf)" -Encoding ascii
Write-Host "已產生 $out"
Write-Host "SHA-256: $hash"
