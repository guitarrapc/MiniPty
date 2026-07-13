# MiniPty VS Code extension sample

This dependency-free sample connects VS Code's `Pseudoterminal` API to the published
`VsCodeTerminalHelper`. It validates real integrated-terminal input, output, resize, UTF-8 framing,
flow-control acknowledgement, and exit ordering.

It does not exercise bridge-managed reconnect. The stdio helper owns one PTY process; persistent
reconnect requires a long-lived service hosting `PtyWebSocketSessionManager`.

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

1. Run **MiniPty: Open Terminal** from the Command Palette.
2. Enter `printf 'MiniPty UTF-8: 日本語 🚀\n'` and confirm the text is not corrupted.
3. Run `stty size`, resize the terminal panel, and run `stty size` again.
4. Run `exit` and confirm the terminal closes without losing the final output.

The extension automatically finds the helper published under `artifacts/vscode-helper/<rid>`.
For another output location, set `minipty.helperPath` in the Extension Development Host or pass
`MINIPTY_VSCODE_HELPER`. `minipty.helperArguments` optionally selects a command other than the
default login shell.
