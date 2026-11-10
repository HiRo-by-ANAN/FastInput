#!/bin/bash
set -e

script_dir="$(cd "$(dirname "$0")" && pwd)"
app_path="$script_dir/../dist/FastInput.app"
if [ ! -d "$app_path" ]; then
  printf 'FastInput.app not found. Build it first with bash scripts/build-macos.sh.\n' >&2
  exit 1
fi

exec open -a "$app_path" --args "$@"
