#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: pack-with-native.sh <package-version> [output-dir]}"
version="${version#v}"
out_dir="${2:-./publish}"
root="$(cd "$(dirname "$0")/.." && pwd)"

required=(
  "linux-x64/native/libminipty_unix.so"
  "linux-x64/native/libminipty_unix.a"
  "linux-arm64/native/libminipty_unix.so"
  "linux-arm64/native/libminipty_unix.a"
  "osx-x64/native/libminipty_unix.dylib"
  "osx-x64/native/minipty_spawn_helper"
  "osx-arm64/native/libminipty_unix.dylib"
  "osx-arm64/native/minipty_spawn_helper"
)

for rel in "${required[@]}"; do
  if [[ ! -f "$root/runtimes/$rel" ]]; then
    echo "Missing native artifact: runtimes/$rel" >&2
    exit 1
  fi
done

mkdir -p "$out_dir"

dotnet pack "$root/src/MiniPty/MiniPty.csproj" -c Release -o "$out_dir" \
  -p:Version="$version" \
  -p:MiniPtyPackUnixNative=true

dotnet pack "$root/src/MiniPty.Capture/MiniPty.Capture.csproj" -c Release -o "$out_dir" \
  -p:Version="$version"

dotnet pack "$root/src/MiniPty.Console/MiniPty.Console.csproj" -c Release -o "$out_dir" \
  -p:Version="$version"

echo "Packed to $out_dir:"
ls -la "$out_dir"/*.nupkg

if command -v unzip >/dev/null 2>&1; then
  nupkg="$(ls "$out_dir"/MiniPty.*.nupkg | head -1)"
  echo "MiniPty nupkg native assets:"
  unzip -l "$nupkg" | grep -E 'runtimes/' || {
    echo "No runtimes/ entries found in $nupkg" >&2
    exit 1
  }
  for rel in "${required[@]}"; do
    if ! unzip -l "$nupkg" | awk '{print $4}' | grep -Fx "runtimes/$rel" >/dev/null; then
      echo "Missing or mis-pathed nupkg entry: runtimes/$rel" >&2
      unzip -l "$nupkg" | grep -E 'spawn_helper|native/' >&2 || true
      exit 1
    fi
  done
fi
