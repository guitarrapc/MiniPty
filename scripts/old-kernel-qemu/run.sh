#!/usr/bin/env bash
# Boots an old Linux kernel under QEMU and runs the test suite in it, for MiniPty's fallbacks that a seccomp filter
# cannot reproduce. CI runs the rest under seccomp (scripts/run-tests-under-seccomp.sh); a filter can refuse a call, but
# it cannot make the kernel ignore a flag or tell a pidfd from another descriptor:
#
#   4.9   ignores CLONE_PIDFD, so clone succeeds and gives no pidfd. TIOCGPTPEER, pidfd_open and close_range are missing.
#   5.2   clone gives pidfds, but epoll refuses them, so the exit watcher polls; waitid(P_PIDFD) is missing.
#   5.3   epoll watches pidfds; waitid(P_PIDFD) is still missing, so children are reaped by pid.
#
# Run it, with both C libraries, before a release that changes the Linux start, the exit watcher, or the reap.
#
# Everything runs in containers, so the host needs only Docker on x86-64 and bash. The kernels are Debian's, from
# snapshot.debian.org, pinned by SHA-1. The tests are built in the .NET SDK image of the C library; the guest's root file
# system is the matching .NET runtime image with strace, packed as an initramfs; the guest's init (./init) runs the
# tests and powers off. QEMU emulates the CPU without KVM (Docker Desktop offers none), so the whole suite takes about
# two minutes per boot on a 32-thread desktop. Downloads and builds are kept in the Docker volume
# minipty-old-kernel-qemu; `docker volume rm minipty-old-kernel-qemu` removes them.
#
# Usage: run.sh <4.9|5.2|5.3> <glibc|musl> [tests [tree-node-filter]]
#        run.sh <4.9|5.2|5.3> <glibc|musl> trace [class...]
#   tests   runs the whole suite, or the tests a tree-node filter selects (the default).
#   trace   runs each class (by default PtyUnixStartTests, PtyUnixDisposeTests and PtyKillProcessTreeTests) under
#           strace, and counts the calls each fallback replaces, by result, so the path each start took shows. Tests
#           bound by time can fail under strace; they are listed, and do not fail the run.
# Exit status: the guest's (in mode tests, the suite's), or 1 when the guest reported none.
set -euo pipefail

usage="Usage: run.sh <4.9|5.2|5.3> <glibc|musl> [tests [tree-node-filter] | trace [class...]]"
kernel="${1:?$usage}"
libc="${2:?$usage}"
mode="${3:-tests}"
shift $(($# < 3 ? $# : 3))
args="$*"

root="$(cd "$(dirname "$0")/../.." && pwd)"
here="$root/scripts/old-kernel-qemu"
# Git Bash would rewrite the container paths below into Windows paths.
export MSYS_NO_PATHCONV=1
volume=minipty-old-kernel-qemu
host_image=minipty-old-kernel-qemu:host

# Debian 9's last kernel (stretch, 4.9.228-1), and the 5.2 and 5.3 kernels of Debian unstable.
case "$kernel" in
  4.9) sha1=dcc2e688656bd7bf7b3d5b16a15265c024c680c4 ;; # linux-image-4.9.0-13-amd64_4.9.228-1_amd64.deb
  5.2) sha1=cecaadbb5b7b13d9ffedbb211b657d3119778d35 ;; # linux-image-5.2.0-3-amd64-unsigned_5.2.17-1_amd64.deb
  5.3) sha1=fe5419966a70d619e74af9cb37b19727766a9713 ;; # linux-image-5.3.0-3-amd64-unsigned_5.3.15-1_amd64.deb
  *) echo "$usage" >&2; exit 2 ;;
esac

case "$libc" in
  glibc)
    sdk="${MINIPTY_QEMU_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
    runtime="${MINIPTY_QEMU_RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/runtime:10.0}"
    sdk_setup="export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq --no-install-recommends gcc libc6-dev >/dev/null"
    rootfs_setup="apt-get update -qq && apt-get install -y -qq procps strace >/dev/null && rm -rf /var/lib/apt/lists/*"
    ;;
  musl)
    sdk="${MINIPTY_QEMU_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0-alpine}"
    runtime="${MINIPTY_QEMU_RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/runtime:10.0-alpine}"
    sdk_setup="apk add -q --no-cache bash build-base"
    # docker export drops the image's environment, and with it the Alpine images' invariant globalization, so the guest
    # needs ICU.
    rootfs_setup="apk add -q --no-cache procps strace icu-libs"
    ;;
  *) echo "$usage" >&2; exit 2 ;;
esac

case "$mode" in
  tests | trace) ;;
  *) echo "$usage" >&2; exit 2 ;;
esac

echo "== Host image (QEMU, and the tools to unpack a kernel and pack an initramfs)"
docker build -q -t "$host_image" - <<'EOF'
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      qemu-system-x86 cpio gzip ca-certificates curl >/dev/null && rm -rf /var/lib/apt/lists/*
EOF

echo "== Kernel $kernel"
docker run --rm -v "$volume:/w" "$host_image" sh -c "
  set -e
  if [ -f /w/vmlinuz-$sha1 ]; then echo 'cached'; exit 0; fi
  curl -fsSL --retry 5 --retry-all-errors -o /tmp/kernel.deb https://snapshot.debian.org/file/$sha1
  echo '$sha1  /tmp/kernel.deb' | sha1sum -c -
  dpkg-deb -x /tmp/kernel.deb /tmp/kernel
  cp /tmp/kernel/boot/vmlinuz-* /w/vmlinuz-$sha1
"

echo "== Tests, built in $sdk"
# The archive is extracted first and the package installation reads no stdin, so the stream reaches tar intact.
tar -C "$root" -cf - --exclude=bin --exclude=obj --exclude=node_modules \
  .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props global.json LICENSE README.md \
  buildTransitive src tests samples |
  docker run -i --rm -v "$volume:/w" "$sdk" sh -c "
    set -e
    mkdir -p /src
    tar -x -C /src
    { $sdk_setup; } </dev/null
    cd /src
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    dotnet build tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release -v quiet -nologo
    rm -rf /w/tests-$libc
    cp -a tests/MiniPty.Tests/bin/Release/net10.0 /w/tests-$libc
  "

echo "== seed-entropy (static, so it runs on either root file system)"
docker run -i --rm -v "$volume:/w" alpine:3.21 sh -c \
  "apk add -q --no-cache build-base linux-headers && cat > /tmp/seed-entropy.c && cc -static -O2 -Wall -o /w/seed-entropy /tmp/seed-entropy.c" \
  < "$here/seed-entropy.c"

echo "== Root file system from $runtime"
rootfs_image="minipty-old-kernel-qemu:rootfs-$libc"
docker build -q -t "$rootfs_image" - <<EOF
FROM $runtime
RUN $rootfs_setup
EOF
cid="$(docker create "$rootfs_image")"
trap 'docker rm -f "$cid" >/dev/null 2>&1 || true' EXIT
docker export "$cid" | docker run -i --rm -v "$volume:/w" "$host_image" sh -c "
  set -e
  rm -rf /w/rootfs
  mkdir /w/rootfs
  tar -x -C /w/rootfs
"

echo "== Boot Linux $kernel with $libc, mode $mode${args:+, $args}"
log="$(mktemp)"
trap 'docker rm -f "$cid" >/dev/null 2>&1 || true; rm -f "$log"' EXIT
docker run -i --rm -v "$volume:/w" \
  -e MODE="$mode" -e ARGS="$args" -e SMP="${MINIPTY_QEMU_SMP:-4}" -e MEM="${MINIPTY_QEMU_MEM:-3072}" \
  "$host_image" sh -c "
  set -e
  R=/w/rootfs
  cat > \$R/init
  chmod +x \$R/init
  rm -rf \$R/opt/tests
  mkdir -p \$R/opt
  cp -a /w/tests-$libc \$R/opt/tests
  cp /w/seed-entropy \$R/opt/seed-entropy
  printf '%s' \"\$MODE\" > \$R/opt/mode
  printf '%s' \"\$ARGS\" > \$R/opt/args
  rm -f \$R/dev/console && mknod -m 600 \$R/dev/console c 5 1
  (cd \$R && find . -print0 | cpio --null -o -H newc --quiet | gzip -1 > /w/initrd.gz)
  echo \"initrd: \$(du -h /w/initrd.gz | cut -f1)\"
  start=\$(date +%s)
  timeout 3000 qemu-system-x86_64 -accel tcg,thread=multi -cpu max -smp \$SMP -m \$MEM \
    -kernel /w/vmlinuz-$sha1 -initrd /w/initrd.gz -append 'console=ttyS0 rdinit=/init panic=-1 quiet' \
    -display none -serial stdio -monitor none -no-reboot </dev/null 2>&1 | tr -d '\r' || echo \"qemu exit: \$?\"
  echo \"wall: \$((\$(date +%s) - start)) s\"
" < "$here/init" | tee "$log"

guest_exit="$(sed -n 's/^GUEST-EXIT \([0-9][0-9]*\).*/\1/p' "$log" | tail -1)"
if [[ -z "$guest_exit" ]]; then
  echo "run.sh: the guest reported no result" >&2
  exit 1
fi
exit "$guest_exit"
