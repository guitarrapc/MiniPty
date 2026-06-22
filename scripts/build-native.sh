#!/usr/bin/env bash
set -euo pipefail

rid="${1:?Usage: build-native.sh <runtime-identifier>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/src/MiniPty/Native/minipty_unix.c"
out_dir="$root/runtimes/$rid/native"

mkdir -p "$out_dir"

case "$rid" in
  linux-*)
    cc -shared -fPIC -O2 -lutil -o "$out_dir/libminipty_unix.so" "$src"
    ;;
  osx-*)
    cc -shared -fPIC -O2 -lutil -o "$out_dir/libminipty_unix.dylib" "$src"
    ;;
  win-*)
    echo "Windows uses in-process ConPTY; no libminipty_unix artifact for $rid."
    exit 0
    ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 1
    ;;
esac

echo "Built $out_dir"
