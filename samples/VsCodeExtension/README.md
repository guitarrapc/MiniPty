# MiniPty VS Code extension sample

This dependency-free sample has two real integrated-terminal paths:

- **MiniPty: Open One-Shot Terminal** connects `Pseudoterminal` to the published
  `VsCodeTerminalHelper` over framed stdio.
- **MiniPty: Open Persistent Terminal** connects to the independently running
  `VsCodePersistentBridge` service, which hosts `PtyWebSocketSessionManager`.

Together they validate input, output, resize, UTF-8 streaming, flow control, ordered exit, bearer
authentication, bounded replay by absolute ACK offset, and reconnect without replacing the PTY.

## Run on macOS

From the MiniPty repository root, build the native artifacts and publish the helper:

```bash
case "$(uname -m)" in
  arm64)  rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

bash scripts/build-native.sh "$rid"

out="$PWD/artifacts/vscode-helper/$rid"
dotnet publish samples/VsCodeTerminalHelper.cs \
  -c Release -r "$rid" --self-contained true \
  -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None \
  -o "$out"

code --new-window --extensionDevelopmentPath="$PWD/samples/VsCodeExtension" "$PWD"
```

For a Homebrew-installed .NET SDK, apply the `LIBRARY_PATH` setup in the repository README before
`dotnet publish`. If `code` is unavailable, run **Shell Command: Install 'code' command in PATH**
from VS Code's Command Palette first.

In the Extension Development Host window:

1. Run **MiniPty: Open One-Shot Terminal** from the Command Palette.
2. Enter `printf 'MiniPty UTF-8: 日本語 🚀\n'` and confirm the text is not corrupted.
3. Run `stty size`, resize the terminal panel, and run `stty size` again.
4. Run `exit` and confirm the terminal closes without losing the final output.

The extension automatically finds the helper published under `artifacts/vscode-helper/<rid>`.
For another output location, set `minipty.helperPath` in the Extension Development Host or pass
`MINIPTY_VSCODE_HELPER`. `minipty.helperArguments` optionally selects a command other than the
default login shell.

## Run on Windows

From PowerShell in the MiniPty repository root, select the current architecture and publish the
one-shot NativeAOT helper:

```powershell
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$rid = if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
    "win-x64"
} elseif ($architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
    "win-arm64"
} else {
    throw "Unsupported Windows architecture: $architecture"
}

$out = Join-Path $PWD "artifacts\vscode-helper\$rid"
dotnet publish samples\VsCodeTerminalHelper.cs `
  -c Release -r $rid --self-contained true `
  -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None `
  -o $out

code --new-window "--extensionDevelopmentPath=$PWD\samples\VsCodeExtension" $PWD
```

In the Extension Development Host, run **MiniPty: Open One-Shot Terminal**. Then run
`chcp 65001 >NUL`, `echo MiniPty UTF-8: 日本語`, resize the terminal panel, and run `exit`.
The final output must be visible before the terminal closes.

## Validate authenticated persistent reconnect

Keep the service independent from VS Code so the PTY survives a transport or extension-client
disconnect. In a separate terminal, generate a 256-bit service access token and start the loopback
service:

```bash
export MINIPTY_BRIDGE_ACCESS_TOKEN="$(openssl rand -hex 32)"
printf 'Copy this token into the VS Code prompt if needed: %s\n' "$MINIPTY_BRIDGE_ACCESS_TOKEN"
dotnet samples/VsCodePersistentBridge.cs
```

The listener is intentionally fixed to `127.0.0.1`. This sample is not a TLS-enabled remote shell
service. Start the Extension Development Host from a shell with the same environment variable, or
paste the token into VS Code's password input when prompted:

```bash
export MINIPTY_BRIDGE_ACCESS_TOKEN="<the same 64-character token>"
code --new-window --extensionDevelopmentPath="$PWD/samples/VsCodeExtension" "$PWD"
```

On Windows, use two PowerShell windows. In the first, generate the token, print it once for copying,
and keep the service running:

```powershell
$token = [Convert]::ToHexString(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$token
$env:MINIPTY_BRIDGE_ACCESS_TOKEN = $token
dotnet samples\VsCodePersistentBridge.cs
```

In the second PowerShell window, paste the same token and start the Extension Development Host:

```powershell
$env:MINIPTY_BRIDGE_ACCESS_TOKEN = "<the same 64-character token>"
code --new-window "--extensionDevelopmentPath=$PWD\samples\VsCodeExtension" $PWD
```

If VS Code reuses an existing process and does not inherit the environment variable, the extension
shows a password input; paste the same token there. It is not stored in VS Code settings.

Then verify:

1. Run **MiniPty: Open Persistent Terminal**.
2. On macOS/Linux, run `export MINIPTY_E2E_STATE=preserved; echo $$`; on Windows run
   `set MINIPTY_E2E_STATE=preserved`.
3. Run **MiniPty: Simulate Persistent Terminal Disconnect**. The terminal stays open and prints a
   new green `[MiniPty: connected]` banner after reconnect.
4. Run `printf '%s %s\n' "$MINIPTY_E2E_STATE" "$$"` on macOS/Linux; the value and PID must match.
   On Windows, run `echo %MINIPTY_E2E_STATE%` and confirm `preserved`. This proves that VS Code
   reattached to the same shell rather than spawning a replacement.
5. Run `exit`; final output must appear before the integrated terminal closes.

The service itself also has a non-interactive E2E that rejects invalid management/session tokens,
disconnects after acknowledged output, reconnects from that absolute offset, verifies preserved
shell state, and exits normally:

```bash
dotnet samples/VsCodePersistentBridge.cs --smoke
```
