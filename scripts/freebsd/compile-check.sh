#!/usr/bin/env bash
# Compiles libminipty_unix for FreeBSD with clang, against the headers and libraries of a FreeBSD release's base
# archive, so the shim's FreeBSD-only code builds in CI without a FreeBSD machine. It compiles what MiniPty.csproj
# compiles when MiniPty is built from source on FreeBSD (cc -shared -fPIC -O2 -pthread ...), with warnings as errors. Then it links an empty program against the library, so a function FreeBSD's libraries lack fails too. The
# library's own link cannot check that with --no-undefined: FreeBSD defines environ in a program's startup code, not in
# libc. The tests run in a FreeBSD guest instead (run-tests-in-qemu.sh).
#
# The archives come from download.freebsd.org, pinned by the SHA-256 in each release's MANIFEST. 14.4 is the oldest
# supported release, 15.1 the newest.
#
# Usage: compile-check.sh <14.4|15.1> <amd64|arm64>
# Needs clang, lld, binutils, curl, xz and tar on a Linux host. MINIPTY_FREEBSD_CACHE names a directory that keeps the archives
# between runs.
set -euo pipefail

usage="Usage: compile-check.sh <14.4|15.1> <amd64|arm64>"
release="${1:?$usage}"
arch="${2:?$usage}"

case "$release/$arch" in
  14.4/amd64) sha256=769f60a6eea2938ad6b7943cfbcc17dfaf7d1f7ba59a32b869a00349302df853 ;;
  14.4/arm64) sha256=c456bf75ee3d0b4da0b0b527402b0a9e61497f02756b2cc5801b51e80b3f90c4 ;;
  15.1/amd64) sha256=3768988b151c20f965679062b065c63a977d6bbb9f47fd83695ec2c40790c18f ;;
  15.1/arm64) sha256=5b7a46a0abfbe23a1d4454b5600e2efcfce16b705bc7e9c851c37470c035ef98 ;;
  *) echo "$usage" >&2; exit 2 ;;
esac

case "$arch" in
  amd64) url_arch=amd64; triple="x86_64-unknown-freebsd$release" ;;
  arm64) url_arch=arm64/aarch64; triple="aarch64-unknown-freebsd$release" ;;
esac

root="$(cd "$(dirname "$0")/../.." && pwd)"
native_dir="$root/src/MiniPty/Native"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cache="${MINIPTY_FREEBSD_CACHE:-$work}"
mkdir -p "$cache"
archive="$cache/base-$release-$arch.txz"

if [[ ! -f "$archive" ]] || ! echo "$sha256  $archive" | sha256sum -c --status; then
  echo "== base.txz of FreeBSD $release-RELEASE ($arch)"
  curl -fsSL --retry 5 --retry-all-errors -o "$archive.part" \
    "https://download.freebsd.org/releases/$url_arch/$release-RELEASE/base.txz"
  echo "$sha256  $archive.part" | sha256sum -c -
  mv "$archive.part" "$archive"
fi

sysroot="$work/sysroot"
mkdir -p "$sysroot"
# FreeBSD's tar records file flags that GNU tar does not know.
tar --warning=no-unknown-keyword -xJf "$archive" -C "$sysroot" ./lib ./usr/include ./usr/lib

echo "== libminipty_unix.so for $triple"
cc=(clang --target="$triple" --sysroot="$sysroot" -fuse-ld=lld)
"${cc[@]}" -shared -fPIC -O2 -pthread -Wall -Wextra -Werror \
  -o "$work/libminipty_unix.so" \
  "$native_dir/minipty_unix.c" "$native_dir/minipty_unix_exec.c" \
  "$native_dir/minipty_unix_exit.c" "$native_dir/minipty_unix_output.c"
readelf -h -d "$work/libminipty_unix.so" | grep -E 'OS/ABI|Machine|NEEDED'

echo "== A program linked against it"
printf 'int main(void) { return 0; }\n' > "$work/main.c"
# lld checks a library's undefined symbols only when every library it needs is in the link, as libc and libthr are.
"${cc[@]}" -pthread -o "$work/link-check" "$work/main.c" \
  -L"$work" -Wl,--no-as-needed -lminipty_unix -Wl,--no-allow-shlib-undefined
echo "ok: FreeBSD $release-RELEASE $arch"
