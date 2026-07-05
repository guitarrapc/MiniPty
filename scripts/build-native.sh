#!/usr/bin/env bash
set -euo pipefail

rid="${1:?Usage: build-native.sh <runtime-identifier>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
native_dir="$root/src/MiniPty/Native"
sources=(
  "$native_dir/minipty_unix.c"
  "$native_dir/minipty_unix_exec.c"
)
out_dir="$root/runtimes/$rid/native"

mkdir -p "$out_dir"

build_static_archive() {
  local out="$1"
  shift
  local src
  local work
  local objs=()

  work="$(mktemp -d)"
  trap 'rm -rf "$work"' RETURN
  for src in "$@"; do
    local base
    base="$(basename "$src")"
    cp "$src" "$work/$base"
    local obj="$work/${base%.c}.o"
    cc -c -fPIC -O2 -I"$native_dir" -o "$obj" "$work/$base"
    objs+=("$obj")
  done

  ar rcs "$out" "${objs[@]}"
}

case "$rid" in
  linux-*)
    cc -shared -fPIC -O2 -lutil -o "$out_dir/libminipty_unix.so" "${sources[@]}"
    build_static_archive "$out_dir/libminipty_unix.a" "${sources[@]}"
    ;;
  osx-*)
    cc -shared -fPIC -O2 -lutil -o "$out_dir/libminipty_unix.dylib" "${sources[@]}"
    build_static_archive "$out_dir/libminipty_unix.a" "${sources[@]}"
    cc -O2 -o "$out_dir/minipty_spawn_helper" "$native_dir/minipty_spawn_helper.c" "$native_dir/minipty_unix_exec.c"
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
