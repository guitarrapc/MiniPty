#!/usr/bin/env bash
# Runs the flood probe's scenarios (src/MiniPty.Benchmarks/FloodProbe.cs) on this Unix machine, MiniPty beside
# Porta.Pty where Porta.Pty runs, and prints one markdown report: the Linux and FreeBSD figures in
# .github/docs/specs/platform_support.md were taken the same way, so a macOS run lines up with them.
#
# Usage: scripts/flood-probe-matrix.sh [rounds] > flood-$(uname -s).md
#   rounds: how many times each throughput case runs, alternating the libraries (default 2).
# Takes about 5 minutes. Run it on an otherwise idle machine; a busy one shows up as wide spreads between rounds.
set -euo pipefail

rounds="${1:-2}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
dotnet build src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -c Release -nologo -v quiet >&2
probe="$root/src/MiniPty.Benchmarks/bin/Release/net10.0/MiniPty.Benchmarks"

run() {
  echo
  echo "### $*"
  echo
  "$probe" --flood-probe "$@" 2>&1 | grep -v '^Flood probe' || echo "(failed: exit $?)"
}

echo "# Flood probe on $(uname -sm), $(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu) CPUs, $(date -u +%Y-%m-%dT%H:%MZ)"
for round in $(seq 1 "$rounds"); do
  run sustained --bytes 268435456 --library minipty
  run sustained --bytes 268435456 --library porta
done
run echo --seconds 5 --library minipty
run echo --seconds 5 --library porta
run neighbors --floods 0,1,4,16 --seconds 5 --library minipty
run neighbors --floods 0,1,4,16 --seconds 5 --library porta
run control --rounds 20 --library minipty
run control --rounds 20 --library porta
run slow --seconds 10 --delay-ms 2 --library minipty
run bridge --seconds 5 --ack-delay-ms 0
run bridge --seconds 5 --ack-delay-ms 10
run oneshot --api complete --decode true --bytes 67108864
