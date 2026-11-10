$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw '請在 Windows 上執行這個建置工具。'
}

function Require-Tool([string]$name, [string]$helpText) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "找不到 $name。$helpText"
    }
}

$projectDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distDir = Join-Path $projectDir 'dist'
$stageDir = Join-Path $distDir '.windows-build-staging'
$stagePackage = Join-Path $stageDir 'FastInput-windows'
$packageDir = Join-Path $distDir 'FastInput-windows'
$archivePath = Join-Path $distDir 'FastInput-windows-x64.zip'
$tempArchive = Join-Path $distDir 'FastInput-windows-x64.building.zip'

Require-Tool 'rustup' '請先安裝 Rust（https://rustup.rs/），並重新開啟終端機。'
Require-Tool 'cargo' '請先安裝 Rust（https://rustup.rs/），並重新開啟終端機。'
Require-Tool 'dotnet' '請先安裝 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）。'
$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw '需要 .NET 8 SDK。請安裝後重新執行；僅有 .NET 執行環境不足以編譯。'
}

# Rust's MSVC linker and Windows SDK are supplied by Visual Studio C++ tools.
$vswhere = if (${env:ProgramFiles(x86)}) {
    Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
} else { $null }
if ($vswhere -and (Test-Path $vswhere)) {
    $vcInstall = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vcInstall) {
        Write-Warning '未找到 Visual Studio 的「使用 C++ 的桌面開發」工具。若 Rust 連結失敗，請先在 Visual Studio Installer 安裝該工作負載。'
    }
}

New-Item $distDir -ItemType Directory -Force | Out-Null
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item $stagePackage -ItemType Directory -Force | Out-Null

# Avoid putting this computer's account and source paths into Rust panic locations.
$privacyFlags = @("--remap-path-prefix=$projectDir=/FastInput")
if ($env:USERPROFILE) {
    $privacyFlags += "--remap-path-prefix=$($env:USERPROFILE)=/build"
}
$privacyArgs = [string]::Join([string][char]31, [string[]]$privacyFlags)
if ($env:CARGO_ENCODED_RUSTFLAGS) {
    $env:CARGO_ENCODED_RUSTFLAGS = "$($env:CARGO_ENCODED_RUSTFLAGS)$([char]31)$privacyArgs"
} else {
    $env:CARGO_ENCODED_RUSTFLAGS = $privacyArgs
}

Push-Location $projectDir
try {
    Write-Host '正在準備 Windows Rust 編譯目標…'
    & rustup target add x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw '安裝 Rust Windows 編譯目標失敗。' }

    Write-Host '正在測試內網服務…'
    & cargo test --release --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) {
        throw 'Rust 測試失敗。若錯誤提到 link.exe 或 Windows SDK，請安裝 Visual Studio Installer 的「使用 C++ 的桌面開發」。'
    }

    Write-Host '正在編譯內網服務…'
    & cargo build --release --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw 'Rust 內網服務編譯失敗。' }

    Write-Host '正在編譯 Windows 原生視窗…'
    & dotnet publish (Join-Path $projectDir 'native/windows/FastInput.Windows.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $stagePackage
    if ($LASTEXITCODE -ne 0) { throw 'Windows 原生視窗編譯失敗。' }

    $serverBinary = Join-Path $projectDir 'target/x86_64-pc-windows-msvc/release/fastinput.exe'
    $appBinary = Join-Path $stagePackage 'FastInput.exe'
    if (-not (Test-Path $serverBinary) -or -not (Test-Path $appBinary)) {
        throw '編譯完成後找不到 FastInput.exe 或內網服務程式。'
    }
    Copy-Item $serverBinary (Join-Path $stagePackage 'fastinput-server.exe')
    Get-ChildItem $stagePackage -Filter '*.pdb' -File | Remove-Item -Force

    if (Test-Path $tempArchive) { Remove-Item $tempArchive -Force }
    Compress-Archive -Path $stagePackage -DestinationPath $tempArchive -CompressionLevel Optimal
    if (-not (Test-Path $tempArchive)) { throw '壓縮 Windows 部署檔失敗。' }

    if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
    Move-Item $stagePackage $packageDir
    if (Test-Path $archivePath) { Remove-Item $archivePath -Force }
    Move-Item $tempArchive $archivePath

    $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "${archivePath}.sha256" -Value "$hash  FastInput-windows-x64.zip" -Encoding Ascii
    Write-Host "`n建置完成：$archivePath"
    Write-Host "可直接執行：$(Join-Path $packageDir 'FastInput.exe')"
    Write-Host "SHA-256：$hash"
}
finally {
    Pop-Location
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $tempArchive) { Remove-Item $tempArchive -Force }
}
