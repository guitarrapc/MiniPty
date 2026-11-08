#!/bin/sh
# Runs in the FreeBSD guest that run-tests-in-qemu.sh boots, as root at the end of its first boot (nuageinit's runcmd),
# with the cidata disk on /mnt. Installs what .NET needs, unpacks .NET and the tests, compiles libminipty_unix with the
# system's cc as MiniPty.csproj does on FreeBSD, runs the suite, prints the results on the serial console, and powers
# off. The host reads GUEST-EXIT for the outcome.
exec </dev/null >/dev/console 2>&1
export HOME=/root PATH=/sbin:/bin:/usr/sbin:/usr/bin:/usr/local/sbin:/usr/local/bin
export DOTNET_CLI_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROOT=/opt/dotnet

setup() {
  echo "GUEST-FREEBSD $(freebsd-version -ku | tr '\n' ' ')$(uname -m), $(sysctl -n hw.ncpu) CPUs"
  # .NET's run-time dependencies, from the FreeBSD package repository.
  ASSUME_ALWAYS_YES=yes pkg bootstrap -f >/dev/null &&
    pkg install -y -q libunwind icu libinotify krb5 &&
    mkdir -p /opt/dotnet /opt/tests &&
    tar -xzf /mnt/dotnet-sdk.tar.gz -C /opt/dotnet &&
    tar -xf /mnt/tests.tar -C /opt/tests &&
    # The tests were built on Linux, so their apphost is a Linux program. Some tests start the test host again by that
    # name, so a script that runs the assembly under FreeBSD's .NET stands in for it.
    printf '#!/bin/sh\nexec /opt/dotnet/dotnet exec /opt/tests/MiniPty.Tests.dll "$@"\n' > /opt/tests/MiniPty.Tests &&
    chmod +x /opt/tests/MiniPty.Tests &&
    cc -shared -fPIC -O2 -pthread -o /opt/tests/libminipty_unix.so \
      /mnt/native/minipty_unix.c /mnt/native/minipty_unix_exec.c \
      /mnt/native/minipty_unix_exit.c /mnt/native/minipty_unix_output.c &&
    echo "GUEST-DOTNET $(/opt/dotnet/dotnet --list-runtimes | grep NETCore)"
}

if setup; then
  cd /tmp || exit 1
  filter=$(cat /mnt/filter)
  start=$(date +%s)
  if [ -n "$filter" ]; then
    /opt/tests/MiniPty.Tests --no-progress --treenode-filter "$filter" > /tmp/out.txt 2>&1
  else
    /opt/tests/MiniPty.Tests --no-progress > /tmp/out.txt 2>&1
  fi
  rc=$?
  grep -vE '^[[:space:]]*$' /tmp/out.txt
  echo "GUEST-WALL $(($(date +%s) - start)) s"
  echo "GUEST-EXIT $rc"
else
  echo "GUEST-SETUP-FAILED"
fi

poweroff
