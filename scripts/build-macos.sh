#!/bin/bash
set -euo pipefail

project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"

# Compiler diagnostics can otherwise embed the builder's home path in the app.
export RUSTFLAGS="${RUSTFLAGS:-} --remap-path-prefix=$HOME=/build"

if [ -z "${DEVELOPER_DIR:-}" ] && [ -d "/Library/Developer/CommandLineTools" ]; then
  export DEVELOPER_DIR="/Library/Developer/CommandLineTools"
fi

rustup target add aarch64-apple-darwin x86_64-apple-darwin
cargo build --release --target aarch64-apple-darwin
cargo build --release --target x86_64-apple-darwin

app_dir="$project_dir/dist/FastInput.app"
contents_dir="$app_dir/Contents"
executable_path="$contents_dir/MacOS/FastInput"
server_path="$contents_dir/MacOS/fastinput-server"
archive_path="$project_dir/dist/FastInput-macos-universal.zip"
swift_build_dir="$project_dir/target/native-macos"

swiftc_tool="${FASTINPUT_SWIFTC:-swiftc}"
sdk_path="${FASTINPUT_MACOS_SDK:-$(xcrun --sdk macosx --show-sdk-path)}"
lipo_tool="${FASTINPUT_LIPO:-lipo}"

mkdir -p "$swift_build_dir" "$project_dir/target/swift-module-cache"
"$swiftc_tool" -O -parse-as-library -target arm64-apple-macos14.0 -sdk "$sdk_path" \
  -module-cache-path "$project_dir/target/swift-module-cache" \
  -o "$swift_build_dir/FastInput-arm64" "$project_dir/native/macos/FastInputApp.swift"
"$swiftc_tool" -O -parse-as-library -target x86_64-apple-macos14.0 -sdk "$sdk_path" \
  -module-cache-path "$project_dir/target/swift-module-cache" \
  -o "$swift_build_dir/FastInput-x86_64" "$project_dir/native/macos/FastInputApp.swift"

mkdir -p "$project_dir/dist"
rm -rf "$app_dir"
mkdir -p "$contents_dir/MacOS" "$contents_dir/Resources"

package_version="$(awk -F '"' '
  /^\[package\]$/ { in_package = 1; next }
  /^\[/ && in_package { exit }
  in_package && /^version = / { print $2; exit }
' "$project_dir/Cargo.toml")"
if [ -z "$package_version" ]; then
  printf 'Could not read the package version from Cargo.toml.\n' >&2
  exit 1
fi

"$lipo_tool" -create \
  "$project_dir/target/aarch64-apple-darwin/release/fastinput" \
  "$project_dir/target/x86_64-apple-darwin/release/fastinput" \
  -output "$server_path"
"$lipo_tool" -create \
  "$swift_build_dir/FastInput-arm64" \
  "$swift_build_dir/FastInput-x86_64" \
  -output "$executable_path"
chmod +x "$executable_path" "$server_path"
"$lipo_tool" "$executable_path" -verify_arch arm64
"$lipo_tool" "$executable_path" -verify_arch x86_64
"$lipo_tool" "$server_path" -verify_arch arm64
"$lipo_tool" "$server_path" -verify_arch x86_64
cp "$project_dir/assets/AppIcon.icns" "$contents_dir/Resources/AppIcon.icns"

cat > "$contents_dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>zh_TW</string>
    <key>CFBundleDisplayName</key><string>FastInput</string>
    <key>CFBundleExecutable</key><string>FastInput</string>
    <key>CFBundleIconFile</key><string>AppIcon.icns</string>
    <key>CFBundleIdentifier</key><string>io.fastinput.app</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>FastInput</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$package_version</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSLocalNetworkUsageDescription</key><string>FastInput 需要在區域網路中連接您的手機，以同步文字。</string>
</dict>
</plist>
PLIST

plutil -lint "$contents_dir/Info.plist"
codesign --force --sign - "$server_path"
codesign --force --sign - "$app_dir"
codesign --verify --strict "$app_dir"

rm -f "$archive_path"
COPYFILE_DISABLE=1 ditto -c -k --norsrc --noextattr --noqtn --noacl --keepParent "$app_dir" "$archive_path"
printf 'Created %s\n' "$archive_path"
