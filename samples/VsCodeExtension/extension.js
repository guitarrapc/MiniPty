"use strict";

const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");
const { TextDecoder } = require("util");
const vscode = require("vscode");

const FRAME_HEADER_LENGTH = 5;
const FRAME_OUTPUT = 1;
const FRAME_INPUT = 2;
const FRAME_CONTROL = 3;
const MAX_FRAME_LENGTH = 16 * 1024 * 1024;

class MiniPtyTerminal {
  constructor(helperPath, helperArguments, diagnostics) {
    this.helperPath = helperPath;
    this.helperArguments = helperArguments;
    this.diagnostics = diagnostics;
    this.writeEmitter = new vscode.EventEmitter();
    this.closeEmitter = new vscode.EventEmitter();
    this.onDidWrite = this.writeEmitter.event;
    this.onDidClose = this.closeEmitter.event;
    this.decoder = new TextDecoder("utf-8");
    this.pending = Buffer.alloc(0);
    this.child = undefined;
    this.dimensions = undefined;
    this.closed = false;
  }

  open(initialDimensions) {
    this.dimensions = initialDimensions;
    this.child = spawn(this.helperPath, this.helperArguments, {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true
    });

    this.child.stdout.on("data", chunk => this.acceptBytes(chunk));
    this.child.stdout.on("error", error => this.fail(error));
    this.child.stderr.on("data", chunk => this.diagnostics.append(chunk.toString("utf8")));
    this.child.on("error", error => this.fail(error));
    this.child.on("close", code => this.finish(code ?? 1));

    if (this.dimensions) {
      this.sendResize(this.dimensions);
    }
  }

  close() {
    if (!this.child) {
      this.finish(undefined);
      return;
    }

    this.child.stdin.end();
    const child = this.child;
    setTimeout(() => {
      if (!this.closed && child.exitCode === null) {
        child.kill();
      }
    }, 2000).unref();
  }

  handleInput(data) {
    this.sendFrame(FRAME_INPUT, Buffer.from(data, "utf8"));
  }

  setDimensions(dimensions) {
    this.dimensions = dimensions;
    if (this.child) {
      this.sendResize(dimensions);
    }
  }

  acceptBytes(chunk) {
    this.pending = this.pending.length === 0 ? chunk : Buffer.concat([this.pending, chunk]);
    while (this.pending.length >= FRAME_HEADER_LENGTH) {
      const type = this.pending[0];
      const length = this.pending.readUInt32LE(1);
      if (length > MAX_FRAME_LENGTH) {
        this.fail(new Error(`MiniPty helper frame is too large: ${length} bytes.`));
        return;
      }
      if (this.pending.length < FRAME_HEADER_LENGTH + length) {
        return;
      }

      const payload = this.pending.subarray(FRAME_HEADER_LENGTH, FRAME_HEADER_LENGTH + length);
      this.pending = this.pending.subarray(FRAME_HEADER_LENGTH + length);
      this.acceptFrame(type, payload);
      if (this.closed) {
        return;
      }
    }
  }

  acceptFrame(type, payload) {
    if (type === FRAME_OUTPUT) {
      const text = this.decoder.decode(payload, { stream: true });
      if (text.length > 0) {
        this.writeEmitter.fire(text);
      }
      this.sendControl({ type: "ack", bytes: payload.length });
      return;
    }

    if (type !== FRAME_CONTROL) {
      this.fail(new Error(`MiniPty helper sent unknown frame type ${type}.`));
      return;
    }

    let message;
    try {
      message = JSON.parse(payload.toString("utf8"));
    } catch (error) {
      this.fail(new Error(`MiniPty helper sent invalid control JSON: ${error.message}`));
      return;
    }

    if (message.type === "exit") {
      this.finish(Number.isInteger(message.exitCode) ? message.exitCode : 1);
    }
  }

  sendResize(dimensions) {
    this.sendControl({
      type: "resize",
      cols: dimensions.columns,
      rows: dimensions.rows
    });
  }

  sendControl(message) {
    this.sendFrame(FRAME_CONTROL, Buffer.from(JSON.stringify(message), "utf8"));
  }

  sendFrame(type, payload) {
    if (!this.child || !this.child.stdin.writable || this.closed) {
      return;
    }

    const frame = Buffer.allocUnsafe(FRAME_HEADER_LENGTH + payload.length);
    frame[0] = type;
    frame.writeUInt32LE(payload.length, 1);
    payload.copy(frame, FRAME_HEADER_LENGTH);
    this.child.stdin.write(frame);
  }

  fail(error) {
    this.diagnostics.appendLine(error.stack ?? error.message ?? String(error));
    vscode.window.showErrorMessage(`MiniPty terminal failed: ${error.message ?? error}`);
    if (this.child && this.child.exitCode === null) {
      this.child.kill();
    }
    this.finish(1);
  }

  finish(exitCode) {
    if (this.closed) {
      return;
    }
    this.closed = true;
    const tail = this.decoder.decode();
    if (tail.length > 0) {
      this.writeEmitter.fire(tail);
    }
    // VS Code forwards onDidWrite to the renderer asynchronously. Yield once so the final
    // terminal bytes are queued before onDidClose tears down the pseudoterminal.
    setImmediate(() => {
      this.closeEmitter.fire(exitCode);
      this.writeEmitter.dispose();
      this.closeEmitter.dispose();
    });
  }
}

function resolveHelperPath() {
  const configured = vscode.workspace.getConfiguration("minipty").get("helperPath", "").trim();
  const helperPath = configured || process.env.MINIPTY_VSCODE_HELPER || defaultHelperPath();
  if (!helperPath) {
    throw new Error("Set minipty.helperPath or MINIPTY_VSCODE_HELPER to the published VsCodeTerminalHelper executable.");
  }
  if (!fs.existsSync(helperPath)) {
    throw new Error(`VsCodeTerminalHelper was not found at: ${helperPath}`);
  }
  return helperPath;
}

function defaultHelperPath() {
  const architecture = process.arch === "x64" ? "x64" : process.arch === "arm64" ? "arm64" : "";
  const platform = process.platform === "win32"
    ? "win"
    : process.platform === "darwin"
      ? "osx"
      : process.platform === "linux"
        ? "linux"
        : "";
  if (!platform || !architecture) {
    return "";
  }

  const executable = process.platform === "win32" ? "VsCodeTerminalHelper.exe" : "VsCodeTerminalHelper";
  return path.resolve(__dirname, "..", "..", "artifacts", "vscode-helper", `${platform}-${architecture}`, executable);
}

function activate(context) {
  const diagnostics = vscode.window.createOutputChannel("MiniPty Sample");
  context.subscriptions.push(diagnostics);
  context.subscriptions.push(vscode.commands.registerCommand("minipty.openTerminal", () => {
    try {
      const helperPath = resolveHelperPath();
      const helperArguments = vscode.workspace.getConfiguration("minipty").get("helperArguments", []);
      const pty = new MiniPtyTerminal(helperPath, helperArguments, diagnostics);
      const terminal = vscode.window.createTerminal({ name: "MiniPty", pty });
      terminal.show();
    } catch (error) {
      diagnostics.appendLine(error.stack ?? error.message ?? String(error));
      vscode.window.showErrorMessage(`MiniPty terminal could not start: ${error.message ?? error}`);
    }
  }));
}

function deactivate() {
}

module.exports = { activate, deactivate };
