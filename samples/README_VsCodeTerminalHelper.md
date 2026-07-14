Install the .NET 10 SDK and Xcode Command Line Tools first. The latter provides the compiler and
linker required by the Unix native shim and NativeAOT. Confirm both are available, then run the
Release build and full test suite:

```bash
dotnet --info
xcode-select -p
dotnet build -c Release
dotnet test -c Release -- --timeout 5m
```

`dotnet test` is the primary behavioral validation for PTY lifecycle and bridge-managed reconnect,
including authentication failures, replay from an acknowledged offset, detached expiry,
backpressure, and concurrent-connection rejection.

Also publish and run the VS Code helper with NativeAOT. This selects the correct runtime identifier
for both Apple Silicon and Intel Macs and uses the same publish flags as CI. Build the Unix native
artifacts first (`runtimes/` is not checked in):

```bash
case "$(uname -m)" in
  arm64)  rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

bash scripts/build-native.sh "$rid"

# Homebrew-installed .NET SDK: NativeAOT's final clang link does not read LDFLAGS/CPPFLAGS.
# Point LIBRARY_PATH at Homebrew's OpenSSL/Brotli/zlib so -lssl/-lbrotli* resolve. The Microsoft
# SDK from dotnet.microsoft.com does not need this on macOS.
if command -v brew >/dev/null 2>&1; then
  export LIBRARY_PATH="$(
    brew --prefix openssl@3 2>/dev/null
  )/lib:$(
    brew --prefix brotli 2>/dev/null
  )/lib:$(
    brew --prefix zlib 2>/dev/null
  )/lib${LIBRARY_PATH:+:$LIBRARY_PATH}"
fi

out="$(mktemp -d)/minipty-vscode-helper"
dotnet publish samples/VsCodeTerminalHelper.cs \
  -c Release -r "$rid" --self-contained true \
  -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None \
  -o "$out"

test -x "$out/VsCodeTerminalHelper"
test -x "$out/minipty_spawn_helper"
test ! -f "$out/libminipty_unix.dylib"
test ! -f "$out/libminipty_unix.a"
"$out/VsCodeTerminalHelper" --smoke
```

Success ends with `VsCodeTerminalHelper smoke passed.` on stderr. The missing `.dylib` and `.a`
checks are intentional: NativeAOT statically links the PTY shim, while the macOS spawn helper remains
an executable sidecar.
