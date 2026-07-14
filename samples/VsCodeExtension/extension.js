"use strict";

const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");
const { TextDecoder, TextEncoder } = require("util");
const vscode = require("vscode");

const SERVICE_ACCESS_TOKEN_ENVIRONMENT_VARIABLE = "MINIPTY_BRIDGE_ACCESS_TOKEN";

const FRAME_HEADER_LENGTH = 5;
const FRAME_OUTPUT = 1;
const FRAME_INPUT = 2;
const FRAME_CONTROL = 3;
const MAX_FRAME_LENGTH = 16 * 1024 * 1024;
const MAX_PENDING_INPUT_LENGTH = 64 * 1024;

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

class PersistentMiniPtyTerminal {
  constructor(serviceUrl, credentials, diagnostics) {
    this.serviceUrl = serviceUrl.replace(/\/$/, "");
    this.credentials = credentials;
    this.diagnostics = diagnostics;
    this.writeEmitter = new vscode.EventEmitter();
    this.closeEmitter = new vscode.EventEmitter();
    this.onDidWrite = this.writeEmitter.event;
    this.onDidClose = this.closeEmitter.event;
    this.decoder = new TextDecoder("utf-8");
    this.encoder = new TextEncoder();
    this.socket = undefined;
    this.dimensions = undefined;
    this.pendingOutput = undefined;
    this.pendingInput = [];
    this.pendingInputLength = 0;
    this.acknowledgedOffset = 0;
    this.generation = 0;
    this.closed = false;
    this.reconnectTimer = undefined;
  }

  open(initialDimensions) {
    this.dimensions = initialDimensions;
    try {
      this.connect();
    } catch (error) {
      this.fail(error);
    }
  }

  close() {
    if (this.closed) {
      return;
    }
    this.closed = true;
    clearTimeout(this.reconnectTimer);
    this.socket?.close(1000, "terminal closed");
    void fetch(`${this.serviceUrl}/sessions/${this.credentials.sessionId}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${this.credentials.authenticationToken}` }
    }).catch(error => this.diagnostics.appendLine(`Terminate failed: ${error.message ?? error}`));
    setImmediate(() => {
      this.writeEmitter.dispose();
      this.closeEmitter.dispose();
    });
  }

  handleInput(data) {
    const bytes = this.encoder.encode(data);
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(bytes);
      return;
    }
    if (this.pendingInputLength + bytes.byteLength <= MAX_PENDING_INPUT_LENGTH) {
      this.pendingInput.push(bytes);
      this.pendingInputLength += bytes.byteLength;
    } else {
      this.diagnostics.appendLine("Persistent terminal input queue is full; input was rejected while disconnected.");
      this.writeEmitter.fire("\x07");
    }
  }

  setDimensions(dimensions) {
    this.dimensions = dimensions;
    this.sendControl({ type: "resize", cols: dimensions.columns, rows: dimensions.rows });
  }

  simulateDisconnect() {
    if (this.closed) {
      return;
    }
    this.writeEmitter.fire("\r\n\x1b[33m[MiniPty: simulating transport disconnect]\x1b[0m\r\n");
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.close(1012, "simulate reconnect");
    } else {
      this.scheduleReconnect();
    }
  }

  connect() {
    if (this.closed || this.socket?.readyState === WebSocket.CONNECTING || this.socket?.readyState === WebSocket.OPEN) {
      return;
    }

    const generation = ++this.generation;
    const url = new URL(`${this.serviceUrl}/sessions/${this.credentials.sessionId}/connect`);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    url.searchParams.set("offset", String(this.acknowledgedOffset));
    const socket = new WebSocket(url, ["minipty", `minipty-token.${this.credentials.authenticationToken}`]);
    socket.binaryType = "arraybuffer";
    this.socket = socket;

    socket.addEventListener("open", () => {
      if (generation !== this.generation || this.closed) {
        socket.close();
        return;
      }
      this.writeEmitter.fire("\r\n\x1b[32m[MiniPty: connected]\x1b[0m\r\n");
      if (this.dimensions) {
        this.sendControl({ type: "resize", cols: this.dimensions.columns, rows: this.dimensions.rows });
      }
      for (const input of this.pendingInput) {
        socket.send(input);
      }
      this.pendingInput.length = 0;
      this.pendingInputLength = 0;
    });
    socket.addEventListener("message", event => {
      if (generation !== this.generation || this.closed) {
        return;
      }
      try {
        this.acceptMessage(event.data);
      } catch (error) {
        this.fail(error);
      }
    });
    socket.addEventListener("error", () => {
      this.diagnostics.appendLine("Persistent bridge WebSocket reported a transport error.");
    });
    socket.addEventListener("close", event => {
      if (generation !== this.generation || this.closed) {
        return;
      }
      this.socket = undefined;
      this.pendingOutput = undefined;
      this.diagnostics.appendLine(`Persistent bridge detached (code ${event.code}); reconnecting from offset ${this.acknowledgedOffset}.`);
      this.scheduleReconnect();
    });
  }

  acceptMessage(data) {
    if (typeof data === "string") {
      const message = JSON.parse(data);
      if (message.type === "output") {
        if (!Number.isSafeInteger(message.offset) || !Number.isSafeInteger(message.bytes) || message.bytes < 0) {
          throw new Error("Persistent bridge sent an invalid output header.");
        }
        if (message.offset !== this.acknowledgedOffset || this.pendingOutput) {
          throw new Error(`Persistent replay offset mismatch: expected ${this.acknowledgedOffset}, received ${message.offset}.`);
        }
        this.pendingOutput = message;
        return;
      }
      if (message.type === "exit") {
        this.finish(Number.isInteger(message.exitCode) ? message.exitCode : 1);
      }
      return;
    }

    const payload = new Uint8Array(data);
    const header = this.pendingOutput;
    if (!header || payload.byteLength !== header.bytes) {
      throw new Error("Persistent bridge binary payload did not match its output header.");
    }
    this.pendingOutput = undefined;
    const text = this.decoder.decode(payload, { stream: true });
    if (text.length > 0) {
      this.writeEmitter.fire(text);
    }
    this.acknowledgedOffset = header.offset + payload.byteLength;
    this.sendControl({ type: "ack", offset: this.acknowledgedOffset });
  }

  sendControl(message) {
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify(message));
    }
  }

  scheduleReconnect() {
    if (this.closed || this.reconnectTimer) {
      return;
    }
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      this.connect();
    }, 250);
  }

  fail(error) {
    this.diagnostics.appendLine(error.stack ?? error.message ?? String(error));
    vscode.window.showErrorMessage(`MiniPty persistent terminal failed: ${error.message ?? error}`);
    this.closed = true;
    clearTimeout(this.reconnectTimer);
    this.socket?.close(1002, "protocol error");
    this.finish(1, true);
  }

  finish(exitCode, alreadyClosed = false) {
    if (this.closed && !alreadyClosed) {
      return;
    }
    this.closed = true;
    clearTimeout(this.reconnectTimer);
    const tail = this.decoder.decode();
    if (tail.length > 0) {
      this.writeEmitter.fire(tail);
    }
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
  let activePersistentTerminal;
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
  context.subscriptions.push(vscode.commands.registerCommand("minipty.openPersistentTerminal", async () => {
    try {
      const serviceUrl = normalizeBridgeUrl(
        vscode.workspace.getConfiguration("minipty").get("bridgeUrl", "http://127.0.0.1:5171"));
      const accessToken = await readServiceAccessToken();
      const response = await fetch(`${serviceUrl.replace(/\/$/, "")}/sessions`, {
        method: "POST",
        headers: { Authorization: `Bearer ${accessToken}` }
      });
      if (!response.ok) {
        throw new Error(`Session creation failed with HTTP ${response.status}.`);
      }
      const credentials = await response.json();
      if (!credentials.sessionId || !credentials.authenticationToken) {
        throw new Error("Persistent bridge returned incomplete session credentials.");
      }
      const pty = new PersistentMiniPtyTerminal(serviceUrl, credentials, diagnostics);
      activePersistentTerminal = pty;
      const terminal = vscode.window.createTerminal({ name: "MiniPty Persistent", pty });
      terminal.show();
    } catch (error) {
      diagnostics.appendLine(error.stack ?? error.message ?? String(error));
      vscode.window.showErrorMessage(`MiniPty persistent terminal could not start: ${error.message ?? error}`);
    }
  }));
  context.subscriptions.push(vscode.commands.registerCommand("minipty.simulateReconnect", () => {
    if (!activePersistentTerminal || activePersistentTerminal.closed) {
      vscode.window.showWarningMessage("Open a MiniPty persistent terminal first.");
      return;
    }
    activePersistentTerminal.simulateDisconnect();
  }));
}

async function readServiceAccessToken() {
  const environmentToken = process.env[SERVICE_ACCESS_TOKEN_ENVIRONMENT_VARIABLE];
  if (environmentToken) {
    return environmentToken;
  }
  const entered = await vscode.window.showInputBox({
    title: "MiniPty persistent bridge access token",
    prompt: `Enter ${SERVICE_ACCESS_TOKEN_ENVIRONMENT_VARIABLE} from the service process.`,
    password: true,
    ignoreFocusOut: true,
    validateInput: value => /^[0-9a-fA-F]{64}$/.test(value) ? undefined : "Enter exactly 64 hexadecimal characters."
  });
  if (!entered) {
    throw new Error("Persistent bridge access token was not provided.");
  }
  return entered;
}

function normalizeBridgeUrl(value) {
  const url = new URL(value);
  const loopback = url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]";
  if (!loopback || url.protocol !== "http:") {
    throw new Error("The sample persistent bridge URL must use http:// on loopback.");
  }
  return url.toString().replace(/\/$/, "");
}

function deactivate() {
}

module.exports = { activate, deactivate };
