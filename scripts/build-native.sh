#!/usr/bin/env bash
# Compiles libminipty_unix (and the macOS spawn helper) for one runtime identifier with the host's toolchain, into
# runtimes/<rid>/native/. The files the NuGet package ships are built by build-native-package.sh, which runs this
# script where the result meets the package's platform floors and then checks it.
set -euo pipefail

rid="${1:?Usage: build-native.sh <runtime-identifier>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
native_dir="$root/src/MiniPty/Native"
sources=(
  "$native_dir/minipty_unix.c"
  "$native_dir/minipty_unix_exec.c"
  "$native_dir/minipty_unix_exit.c"
  "$native_dir/minipty_unix_output.c"
)
out_dir="$root/runtimes/$rid/native"

# The oldest macOS the binaries declare. NativeAOT links macOS applications for 12.0 (AppleMinOSVersion in the
# ILCompiler package's targets), so the static archive must not declare a newer one, and the library and the helper
# follow it. Availability errors make a call to an API newer than this fail the build.
macos_min=12.0

mkdir -p "$out_dir"

build_static_archive() {
  local out="$1"
  shift
  local src
  local work
  local objs=()

  work="$(mktemp -d)"
  trap 'rm -rf "$work"; trap - RETURN' RETURN
  for src in "$@"; do
    local base
    base="$(basename "$src")"
    cp "$src" "$work/$base"
    local obj="$work/${base%.c}.o"
    cc -c "${cflags[@]}" -I"$native_dir" -o "$obj" "$work/$base"
    objs+=("$obj")
  done

  ar rcs "$out" "${objs[@]}"
}

case "$rid" in
  linux-*)
    cflags=(-O2 -fPIC -pthread)
    # -lutil follows the sources: glibc before 2.34 keeps forkpty in libutil, and with --as-needed (the default on
    # Ubuntu) a library named before the objects that need it is dropped, leaving forkpty unresolved at load time.
    cc -shared "${cflags[@]}" -o "$out_dir/libminipty_unix.so" "${sources[@]}" -lutil
    build_static_archive "$out_dir/libminipty_unix.a" "${sources[@]}"
    ;;
  osx-*)
    cflags=(-O2 -fPIC "-mmacosx-version-min=$macos_min" -Werror=unguarded-availability)
    cc -shared "${cflags[@]}" -o "$out_dir/libminipty_unix.dylib" "${sources[@]}" -lutil -lproc
    build_static_archive "$out_dir/libminipty_unix.a" "${sources[@]}"
    cc -O2 "-mmacosx-version-min=$macos_min" -Werror=unguarded-availability \
      -o "$out_dir/minipty_spawn_helper" "$native_dir/minipty_spawn_helper.c" "$native_dir/minipty_unix_exec.c"
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
