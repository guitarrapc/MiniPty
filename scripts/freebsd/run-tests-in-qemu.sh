#!/usr/bin/env bash
# Boots FreeBSD under QEMU and runs the test suite in it. FreeBSD has no hosted runner, and the package ships no FreeBSD
# binary, so this is the only run of MiniPty's FreeBSD paths: the fork start, kqueue, and FreeBSD's tty.
#
# Everything runs in containers, so the host needs only Docker on x86-64 and bash.
#   - The guest is FreeBSD 14.5's cloud-init image from download.freebsd.org, pinned by SHA-256.
#   - Microsoft publishes no .NET for FreeBSD, and FreeBSD's packages stop at .NET 9. .NET 10 comes from
#     github.com/Thefrank/dotnet-freebsd-crossbuild, which builds it from Microsoft's sources for FreeBSD 14, pinned by
#     SHA-512. When the release's download link fails, the same asset is fetched through the API.
#   - The tests are built in the .NET SDK image without the Linux shim. The guest compiles libminipty_unix with its own
#     cc, as MiniPty.csproj does on FreeBSD, and installs .NET's dependencies with pkg, so it needs the network.
# The guest reads its instructions (./user-data) from a disk labelled cidata through nuageinit, FreeBSD's cloud-init,
# runs ./guest.sh at the end of its first boot, prints the results on the serial console, and powers off. With /dev/kvm,
# as on CI's Linux runners, QEMU uses KVM; without it, as under Docker Desktop, it emulates the CPU, which is far
# slower. Downloads and builds are kept in the Docker volume minipty-freebsd; `docker volume rm minipty-freebsd`
# removes them.
#
# Usage: run-tests-in-qemu.sh [tree-node-filter]
# Exit status: the suite's, or 1 when the guest reported none.
set -euo pipefail

filter="${1:-}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
here="$root/scripts/freebsd"
# Git Bash would rewrite the container paths below into Windows paths.
export MSYS_NO_PATHCONV=1
volume=minipty-freebsd
host_image=minipty-freebsd:host
sdk="${MINIPTY_QEMU_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"

image=FreeBSD-14.5-RELEASE-amd64-BASIC-CLOUDINIT-ufs.qcow2
image_url="https://download.freebsd.org/releases/VM-IMAGES/14.5-RELEASE/amd64/Latest/$image.xz"
image_sha256=a2a8cd84cd59077225a0e75ead852e8a08fe26f9ab251a96d5a4fd4fe1db4454
dotnet=dotnet-sdk-10.0.113-freebsd-x64.tar.gz
dotnet_url="https://github.com/Thefrank/dotnet-freebsd-crossbuild/releases/download/v10.0.113-amd64-freebsd-14/$dotnet"
# The same file through the API (asset 642238327 of that release). On 2026-11-08 the download link above returned 504
# on every try, from CI and locally, and for other repositories' releases too, while this endpoint served the file.
dotnet_asset_url="https://api.github.com/repos/Thefrank/dotnet-freebsd-crossbuild/releases/assets/642238327"
dotnet_sha512=3fc6f4b3424cbe3716499823f612418d16934022f1e47e6d7c13dba9139490d391dfac76b3dc62004a4fd34d62f48c0fc312e76959793ca9fb85ea125f330ff4

kvm=()
accel="tcg,thread=multi"
cpu=max
if [[ -e /dev/kvm ]]; then
  kvm=(--device /dev/kvm)
  accel=kvm
  cpu=host
fi

echo "== Host image (QEMU, and the tools to build the cidata disk)"
docker build -q -t "$host_image" - <<'EOF'
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      qemu-system-x86 qemu-utils genisoimage xz-utils ca-certificates curl >/dev/null && rm -rf /var/lib/apt/lists/*
EOF

echo "== FreeBSD 14.5 image and .NET for FreeBSD"
docker run --rm -v "$volume:/w" "$host_image" sh -c "
  set -e
  if [ -f /w/$image ]; then
    echo 'image cached'
  else
    curl -fsSL --retry 5 --retry-all-errors -o /w/image.xz '$image_url'
    echo '$image_sha256  /w/image.xz' | sha256sum -c -
    xz -d -T0 -c /w/image.xz > /w/$image.part
    mv /w/$image.part /w/$image
    rm /w/image.xz
  fi
  if [ -f /w/$dotnet ]; then
    echo 'dotnet cached'
  else
    curl -fsSL --retry 5 --retry-all-errors -o /w/$dotnet.part '$dotnet_url' ||
      curl -fsSL --retry 5 --retry-all-errors -H 'Accept: application/octet-stream' -o /w/$dotnet.part '$dotnet_asset_url'
    echo '$dotnet_sha512  /w/$dotnet.part' | sha512sum -c -
    mv /w/$dotnet.part /w/$dotnet
  fi
"

echo "== Tests, built in $sdk"
# The archive is extracted first and the build reads no stdin, so the stream reaches tar intact.
tar -C "$root" -cf - --exclude=bin --exclude=obj --exclude=node_modules \
  .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props global.json LICENSE README.md \
  buildTransitive src tests samples |
  docker run -i --rm -v "$volume:/w" "$sdk" sh -c "
    set -e
    mkdir -p /src
    tar -x -C /src
    cd /src
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    dotnet build tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release -v quiet -nologo -p:MiniPtyBuildUnixNative=false </dev/null
    rm -f /w/tests.tar
    tar -C tests/MiniPty.Tests/bin/Release/net10.0 -cf /w/tests.tar .
    rm -rf /w/native
    cp -a src/MiniPty/Native /w/native
  "

echo "== Boot FreeBSD 14.5 (accel ${accel%%,*})${filter:+, filter $filter}"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
tar -C "$here" -cf - user-data guest.sh |
  docker run -i --rm "${kvm[@]}" -v "$volume:/w" \
    -e FILTER="$filter" -e ACCEL="$accel" -e CPU="$cpu" \
    -e SMP="${MINIPTY_QEMU_SMP:-4}" -e MEM="${MINIPTY_QEMU_MEM:-4096}" -e TIMEOUT="${MINIPTY_QEMU_TIMEOUT:-5400}" \
    "$host_image" sh -c "
  set -e
  rm -rf /w/cidata /w/run.qcow2
  mkdir /w/cidata
  tar -x -C /w/cidata
  printf 'instance-id: minipty\nlocal-hostname: minipty\n' > /w/cidata/meta-data
  printf '%s' \"\$FILTER\" > /w/cidata/filter
  genisoimage -quiet -V cidata -r -o /w/cidata.iso -graft-points \
    /=/w/cidata dotnet-sdk.tar.gz=/w/$dotnet tests.tar=/w/tests.tar native/=/w/native
  # A fresh overlay per boot, so every boot is a first boot and nuageinit runs; growfs takes the extra space.
  qemu-img create -q -f qcow2 -F qcow2 -b /w/$image /w/run.qcow2 20G
  start=\$(date +%s)
  timeout \$TIMEOUT qemu-system-x86_64 -accel \$ACCEL -cpu \$CPU -smp \$SMP -m \$MEM \
    -drive file=/w/run.qcow2,if=virtio,format=qcow2 -cdrom /w/cidata.iso -nic user,model=virtio-net-pci \
    -display none -serial stdio -monitor none -no-reboot </dev/null 2>&1 | sed -u 's/\r//g' || echo \"qemu exit: \$?\"
  echo \"wall: \$((\$(date +%s) - start)) s\"
" | tee "$log"

guest_exit="$(sed -n 's/^GUEST-EXIT \([0-9][0-9]*\).*/\1/p' "$log" | tail -1)"
if [[ -z "$guest_exit" ]]; then
  echo "run-tests-in-qemu.sh: the guest reported no result" >&2
  exit 1
fi
exit "$guest_exit"
