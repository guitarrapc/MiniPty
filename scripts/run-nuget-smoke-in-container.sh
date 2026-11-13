#!/usr/bin/env bash
# Runs run-nuget-smoke.sh inside a container, for Linux distributions the hosted runners do not provide: the oldest
# glibc .NET 10 supports (an RHEL 8-compatible image) and Alpine, which needs the musl files. The container installs
# NativeAOT prerequisites and copies the .NET 10 SDK from an SDK image, then runs the same smoke as the runners:
# the packed packages under `dotnet run`, then a NativeAOT publish for the runtime identifier.
#
# Both images must be of the host's architecture and use the same libc. Files go in with `docker cp`, which needs
# nothing inside the image. Copying the SDK avoids installer and SDK downloads through the target's older tools.
#
# Usage: run-nuget-smoke-in-container.sh <package-version> <nupkg-directory> <runtime-identifier> <image> <sdk-image>
set -euo pipefail

usage="Usage: run-nuget-smoke-in-container.sh <package-version> <nupkg-directory> <runtime-identifier> <image> <sdk-image>"
version="${1:?$usage}"
feed_dir="${2:?$usage}"
rid="${3:?$usage}"
image="${4:?$usage}"
sdk_image="${5:?$usage}"
root="$(cd "$(dirname "$0")/.." && pwd)"
# Git Bash would rewrite the container paths below into Windows paths.
export MSYS_NO_PATHCONV=1

case "$image" in
  alpine*)
    # .NET's runtime dependencies on Alpine, and clang, build-base, and zlib-dev for NativeAOT.
    setup="apk add --no-cache bash curl ca-certificates icu-libs icu-data-full krb5-libs libgcc libintl libssl3 libstdc++ zlib clang build-base zlib-dev"
    ;;
  almalinux* | rockylinux*)
    setup="dnf install -y -q clang zlib-devel libicu tar gzip findutils"
    ;;
  *)
    echo "run-nuget-smoke-in-container: no setup known for $image" >&2
    exit 1
    ;;
esac

stage="$(mktemp -d)"
cid=""
sdk_cid=""
cleanup() {
  [[ -n "$cid" ]] && docker rm -f "$cid" >/dev/null 2>&1 || true
  [[ -n "$sdk_cid" ]] && docker rm -f "$sdk_cid" >/dev/null 2>&1 || true
  rm -rf "$stage"
}
trap cleanup EXIT

mkdir -p "$stage/scripts" "$stage/samples/SingleFiles" "$stage/feed"
cp "$root/scripts/run-nuget-smoke.sh" "$stage/scripts/"
cp "$root/samples/SingleFiles/NuGetSmoke.cs" "$stage/samples/SingleFiles/"
cp "$feed_dir"/*.nupkg "$stage/feed/"

cid="$(docker create "$image" sh -c "
  set -e
  $setup
  export PATH=/usr/share/dotnet:\$PATH DOTNET_ROOT=/usr/share/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  cd /src
  bash scripts/run-nuget-smoke.sh '$version' feed '$rid'
")"
stage_docker="$stage"
if command -v cygpath >/dev/null 2>&1; then
  stage_docker="$(cygpath -w "$stage")"
fi
docker cp "$stage_docker/." "$cid:/src"
sdk_cid="$(docker create "$sdk_image")"
# Stream Docker's archive directly between stopped containers; no target tools or host SDK are used.
docker cp "$sdk_cid:/usr/share/dotnet" - | docker cp - "$cid:/usr/share"
docker start -a "$cid"
