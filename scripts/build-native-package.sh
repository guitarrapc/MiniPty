#!/usr/bin/env bash
# Builds the native files the NuGet package ships for one runtime identifier, at the platform floors the package
# promises, and checks the result. CI builds every shipped file through this script.
#
#   linux-x64, linux-arm64             glibc 2.27, the floor of the .NET 10 runtime itself: built in Ubuntu 18.04,
#                                      so every libc symbol binds to its oldest version
#   linux-musl-x64, linux-musl-arm64   musl 1.2.3, the runtime's musl floor: built in Alpine 3.17
#   osx-x64, osx-arm64                 macOS 12.0 (see build-native.sh): built on the host
#
# Linux builds run in a container of the host's own architecture, never under emulation. The sources go in and the
# files come out through tar on stdin and stdout, so the build needs no bind mount and leaves no file owned by the
# container's user.
#
# Usage: build-native-package.sh <rid>          build runtimes/<rid>/native/ and check it
#        build-native-package.sh --check <rid>  check runtimes/<rid>/native/ only
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

glibc_floor=2.27
macos_min=12.0
glibc_image="ubuntu:18.04@sha256:152dc042452c496007f07ca9127571cb9c29697f42acbfad72324b2bb2e43c98"
musl_image="alpine:3.17@sha256:8fc3dacfb6d69da8d44e42390de777e48577085db99aa4e4af35f483eb08b989"

fail() {
  echo "build-native-package: $*" >&2
  exit 1
}

# ELF machine name readelf prints for the RID's architecture.
elf_machine() {
  case "$1" in
    *-x64) echo "X86-64" ;;
    *-arm64) echo "AArch64" ;;
    *) fail "no architecture known for $1" ;;
  esac
}

# readelf's output is captured before it is matched: under pipefail, `readelf | grep -q` fails when grep exits first.
check_elf_machine() {
  local file="$1" rid="$2" expected header
  expected="$(elf_machine "$rid")"
  header="$(readelf -h "$file")"
  [[ "$header" == *"Machine:"*"$expected"* ]] || fail "$file is not built for $expected"
}

check() {
  local rid="$1"
  local dir="$root/runtimes/$rid/native"
  local so="$dir/libminipty_unix.so"
  local archive="$dir/libminipty_unix.a"

  case "$rid" in
    linux-musl-*)
      check_elf_machine "$so" "$rid"
      local dynamic
      dynamic="$(readelf -d "$so")"
      [[ "$dynamic" == *"[libc.musl-"* ]] || fail "$so does not link musl's libc"
      [[ "$dynamic" != *"[libc.so.6]"* ]] || fail "$so links glibc"
      echo "ok $rid: links musl's libc"
      ;;
    linux-*)
      check_elf_machine "$so" "$rid"
      local highest top
      highest="$(readelf -V "$so" | grep -o 'Name: GLIBC_[0-9][0-9.]*' | sed 's/Name: GLIBC_//' | sort -uV | tail -1)"
      [[ -n "$highest" ]] || fail "$so requires no versioned glibc symbol, so it was not linked against glibc"
      top="$(printf '%s\n%s\n' "$glibc_floor" "$highest" | sort -V | tail -1)"
      [[ "$top" == "$glibc_floor" ]] || fail "$so requires GLIBC_$highest, above the floor $glibc_floor"
      # An archive records no versions. Names that only newer glibc headers produce (the C23 scanf and strtol
      # variants from glibc 2.38) fail the link of an application built against an older glibc.
      if nm -u "$archive" | grep -E '__isoc(23|2x)_'; then
        fail "$archive references glibc 2.38 symbols"
      fi
      echo "ok $rid: highest glibc version required is $highest (floor $glibc_floor)"
      ;;
    osx-*)
      local file minos
      for file in libminipty_unix.dylib libminipty_unix.a minipty_spawn_helper; do
        # otool prints one LC_BUILD_VERSION per Mach-O file, and one per member of the archive.
        minos="$(otool -l "$dir/$file" | awk '$1 == "minos" { print $2 }' | sort -u | tr '\n' ' ')"
        [[ "$minos" == "$macos_min " ]] || fail "$file declares minos ${minos:-none}, expected $macos_min"
      done
      echo "ok $rid: every file declares minos $macos_min"
      ;;
    *)
      fail "unsupported runtime identifier: $rid"
      ;;
  esac
}

build_in_container() {
  local rid="$1" image setup daemon_arch
  case "$rid" in
    linux-musl-*)
      image="$musl_image"
      setup="apk add --no-cache bash gcc musl-dev binutils"
      ;;
    linux-*)
      image="$glibc_image"
      setup="export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq --no-install-recommends gcc libc6-dev binutils"
      ;;
  esac

  daemon_arch="$(docker version --format '{{.Server.Arch}}')"
  case "$rid:$daemon_arch" in
    *-x64:amd64 | *-arm64:arm64) ;;
    *) fail "$rid must be built on a $(elf_machine "$rid") Docker host, not under emulation (this one is $daemon_arch)" ;;
  esac

  # The archive is extracted first and the package installation reads no stdin, so the stream reaches tar intact.
  tar -C "$root" -cf - scripts/build-native.sh scripts/build-native-package.sh src/MiniPty/Native |
    docker run --rm -i "$image" sh -c "
      set -e
      mkdir -p /src
      tar -x -C /src
      { $setup; } </dev/null >&2
      cd /src
      bash scripts/build-native.sh '$rid' >&2
      bash scripts/build-native-package.sh --check '$rid' >&2
      tar -cf - runtimes/'$rid'
    " |
    tar -C "$root" -xf -
}

if [[ "${1:-}" == "--check" ]]; then
  check "${2:?Usage: build-native-package.sh --check <runtime-identifier>}"
  exit 0
fi

rid="${1:?Usage: build-native-package.sh <runtime-identifier>}"
case "$rid" in
  linux-x64 | linux-arm64 | linux-musl-x64 | linux-musl-arm64)
    build_in_container "$rid"
    ;;
  osx-x64 | osx-arm64)
    bash "$root/scripts/build-native.sh" "$rid"
    check "$rid"
    ;;
  *)
    fail "unsupported runtime identifier: $rid"
    ;;
esac

echo "Built and checked $root/runtimes/$rid/native"
