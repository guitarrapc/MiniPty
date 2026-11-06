#!/usr/bin/env bash
# Runs the test suite on MiniPty's fallbacks for older Linux kernels, under the seccomp profiles in scripts/seccomp/.
# CI's kernels have every call MiniPty's Linux start uses, and a container runs on its host's kernel, so an old
# distribution's image never takes those fallbacks. Each profile makes the kernel refuse some of the newer calls instead:
#
#   old-kernel        what a kernel before 4.13 lacks: TIOCGPTPEER, CLONE_PIDFD, waitid(P_PIDFD), pidfd_open,
#                     pidfd_send_signal, close_range, clone3. No session has a pidfd.
#   no-clone-pidfd    CLONE_PIDFD only, so the start opens the pidfd by pid.
#   no-waitid-pidfd   waitid(P_PIDFD) only, as on Linux 5.2 and 5.3, so children are reaped by pid.
#
# The whole suite runs under each: every session starts and ends on these paths, and a run takes about a minute.
#
# The tests and scripts/seccomp/seccomp-probe.c are built once, in a container of the image under Docker's default
# profile, which is then saved as an image of its own. Before any profile, the probe runs under Docker's default profile
# expecting old-kernel, and must fail: that shows it notices a profile that refuses nothing. Under each profile the probe
# runs first and must pass, so a profile that stopped refusing fails here instead of letting the suite pass on the newer
# paths.
#
# A filter can refuse a call, but cannot make the kernel ignore a flag (Linux 4.9 ignores CLONE_PIDFD) or tell a pidfd
# from another descriptor (Linux 5.2's epoll refuses pidfds). scripts/old-kernel-qemu/ boots those kernels instead.
#
# The image is a .NET SDK image: Debian or Ubuntu based for glibc, Alpine based for musl, of the host's architecture.
# Sources go in through tar on stdin, so the run needs no bind mount and leaves no file behind.
#
# Usage: run-tests-under-seccomp.sh <sdk-image> [profile...]
set -euo pipefail

usage="Usage: run-tests-under-seccomp.sh <sdk-image> [old-kernel|no-clone-pidfd|no-waitid-pidfd...]"
image="${1:?$usage}"
shift
profiles=("$@")
if [[ ${#profiles[@]} -eq 0 ]]; then
  profiles=(old-kernel no-clone-pidfd no-waitid-pidfd)
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
# Profiles are passed by relative path: docker reads them on the client, which on Windows takes no Git Bash path.
cd "$root"
# Git Bash would rewrite the container paths below into Windows paths.
export MSYS_NO_PATHCONV=1

case "$image" in
  *alpine*)
    setup="apk add --no-cache bash build-base"
    ;;
  *)
    setup="export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq --no-install-recommends gcc libc6-dev"
    ;;
esac

for profile in "${profiles[@]}"; do
  [[ -f "$root/scripts/seccomp/$profile.json" ]] || { echo "run-tests-under-seccomp: no profile $profile" >&2; exit 1; }
done

builder="minipty-seccomp-build-$$"
tag="minipty-seccomp:$$"
cleanup() {
  docker rm -f "$builder" >/dev/null 2>&1 || true
  docker rmi -f "$tag" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "::group::Build the tests and the probe in $image"
# The archive is extracted first and the package installation reads no stdin, so the stream reaches tar intact.
tar -C "$root" -cf - --exclude=bin --exclude=obj --exclude=node_modules \
  .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props global.json LICENSE README.md \
  buildTransitive src tests samples scripts/seccomp |
  docker run -i --name "$builder" "$image" sh -c "
    set -e
    mkdir -p /src
    tar -x -C /src
    { $setup; } </dev/null
    cd /src
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    dotnet build tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release
    cc -O2 -Wall -Wextra -o /opt/seccomp-probe scripts/seccomp/seccomp-probe.c
  "
docker commit "$builder" "$tag" >/dev/null
docker rm "$builder" >/dev/null
echo "::endgroup::"

tests="/src/tests/MiniPty.Tests/bin/Release/net10.0/MiniPty.Tests"
failed=0

echo "== Probe under Docker's default profile, expecting old-kernel: it must fail"
if docker run --rm "$tag" /opt/seccomp-probe old-kernel; then
  echo "::error::The probe passed under Docker's default profile, so it would not notice a profile that refuses nothing"
  exit 1
fi

for profile in "${profiles[@]}"; do
  echo "== Profile $profile"
  # --init: an init process reaps the children the tests orphan, as on a host.
  if docker run --rm --init --security-opt "seccomp=scripts/seccomp/$profile.json" \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e TESTINGPLATFORM_TELEMETRY_OPTOUT=1 -w /tmp "$tag" \
    sh -c "/opt/seccomp-probe '$profile' && '$tests' --no-progress"; then
    echo "Profile $profile: passed"
  else
    echo "::error::Profile $profile: the probe or the tests failed"
    failed=1
  fi
done

exit "$failed"
