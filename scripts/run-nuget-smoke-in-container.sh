#!/usr/bin/env bash
# Runs run-nuget-smoke.sh inside a container, for Linux distributions the hosted runners do not provide: the oldest
# glibc .NET 10 supports (an RHEL 8-compatible image) and Alpine, which needs the musl files. The container installs
# the .NET 10 SDK and the NativeAOT prerequisites, then runs the same smoke as the runners: the packed packages under
# `dotnet run`, then a NativeAOT publish for the runtime identifier.
#
# The image must be of the host's architecture. Files go in with `docker cp`, which needs nothing inside the image.
#
# Usage: run-nuget-smoke-in-container.sh <package-version> <nupkg-directory> <runtime-identifier> <image>
set -euo pipefail

usage="Usage: run-nuget-smoke-in-container.sh <package-version> <nupkg-directory> <runtime-identifier> <image>"
version="${1:?$usage}"
feed_dir="${2:?$usage}"
rid="${3:?$usage}"
image="${4:?$usage}"
root="$(cd "$(dirname "$0")/.." && pwd)"

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
cleanup() {
  [[ -n "$cid" ]] && docker rm -f "$cid" >/dev/null 2>&1 || true
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
  # AlmaLinux 8's curl predates --retry-all-errors, so retry connection resets explicitly.
  for attempt in 1 2 3; do
    if curl -sSfL --connect-timeout 30 --max-time 120 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh; then
      break
    fi
    if [ \"\$attempt\" -eq 3 ]; then
      echo 'Failed to download dotnet-install.sh after 3 attempts.' >&2
      exit 1
    fi
    echo \"Retrying dotnet-install.sh download after attempt \$attempt.\" >&2
    sleep 2
  done
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
  export PATH=/usr/share/dotnet:\$PATH DOTNET_ROOT=/usr/share/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  cd /src
  bash scripts/run-nuget-smoke.sh '$version' feed '$rid'
")"
docker cp "$stage/." "$cid:/src"
docker start -a "$cid"
