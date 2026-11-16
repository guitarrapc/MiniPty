// MiniPty Playground viewer: replays scenes that playground/Record.cs recorded on real PTYs.
//
// Left: the terminal, as the user sees it. Right: every call that crossed MiniPty's API, in order: the keys written
// with WriteInputAsync, the chunks read with ReadOutputAsync, resizes, InputMode changes, disposal, and the exit.
// Below: host code whose lines light up as the events they handle happen.
//
// Guided mode stops before each user action and shows it as the next step; the play button plays it at the recorded
// pace. Nothing is synthesized: every byte comes from a recording.
//
// The terminal window sits on a desk sized for the largest terminal any scene uses, and the desk scales with its
// column. A resize moves the window's edges on the desk but never the layout, so the terminal, the log, and the code
// keep one ratio at every width and in every scene.
"use strict";

const SIGNALS_LINUX = { 1: "SIGHUP", 2: "SIGINT", 3: "SIGQUIT", 6: "SIGABRT", 9: "SIGKILL", 10: "SIGUSR1", 11: "SIGSEGV", 12: "SIGUSR2", 13: "SIGPIPE", 15: "SIGTERM" };
const SIGNALS_BSD = { 1: "SIGHUP", 2: "SIGINT", 3: "SIGQUIT", 6: "SIGABRT", 9: "SIGKILL", 11: "SIGSEGV", 13: "SIGPIPE", 15: "SIGTERM", 30: "SIGUSR1", 31: "SIGUSR2" };

const PRIVATE_MODES = {
  1: "application cursor keys", 7: "auto-wrap", 12: "cursor blink", 25: "cursor visible", 47: "alternate screen",
  1000: "mouse tracking", 1002: "mouse drag tracking", 1003: "all mouse motion", 1004: "focus events",
  1006: "SGR mouse reports", 1049: "alternate screen", 2004: "bracketed paste",
  9001: "win32-input-mode (ConPTY asks for key events)",
};
const CSI_FINALS = {
  m: "SGR: colors and style", H: "cursor position", f: "cursor position", J: "erase in display", K: "erase in line",
  X: "erase characters", A: "cursor up", B: "cursor down", C: "cursor forward", D: "cursor back", G: "cursor column",
  d: "cursor row", n: "device status report", c: "device attributes", t: "window operation", r: "scroll region",
  "@": "insert characters", P: "delete characters", L: "insert lines", M: "delete lines", S: "scroll up", T: "scroll down",
};

// The host code beside the terminal. The tag names the events a line handles; those lines light up as they happen.
const CODE = [
  ["// terminal: your UI, such as xterm.js over a WebSocket or a GUI control", null],
  ["await using var session = Pty.Start(new PtyStartInfo", "start"],
  ["{", "start"],
  ["    FileName = shell,                  // bash, or powershell.exe on Windows", "start"],
  ["    Size = new PtySize(80, 22),", "start"],
  ["});", "start"],
  ["", null],
  ["terminal.KeyPressed += async bytes =>", "input"],
  ["    await session.WriteInputAsync(bytes);         // keys, Enter, Ctrl+C", "input"],
  ["terminal.Resized += (cols, rows) =>", "resize"],
  ["    session.Resize(new PtySize(cols, rows));", "resize"],
  ["terminal.Closed += async () =>", "dispose"],
  ["    await session.DisposeAsync();                 // ends the shell", "dispose"],
  ["", null],
  ["await foreach (var chunk in session.ReadOutputAsync())", "output"],
  ["{", null],
  ["    terminal.Write(chunk.Data.Span);              // what the user sees", "output"],
  ["    if (session.InputMode is { Echo: false, Canonical: true })   // echo off, line input on", "inputMode"],
  ["        terminal.MarkSecretInput();               // a password prompt: do not log the keys", "inputMode"],
  ["}", null],
  ["", null],
  ["var status = session.ExitStatus;                 // how the shell ended", "exit"],
];
const TAG_OF = { started: "start", input: "input", resize: "resize", dispose: "dispose", disposed: "dispose", output: "output", eof: "output", inputMode: "inputMode", exit: "exit" };
const KEYWORDS = new Set(["await", "using", "var", "new", "null", "true", "false", "return", "async", "foreach", "in", "if", "is"]);
// Names in CODE that are MiniPty's API. `terminal` and its members are the reader's UI, which MiniPty does not supply;
// the two styles keep anyone who copies the code from looking for a terminal.MarkSecretInput in MiniPty.
const API_NAMES = new Set(["Pty", "Start", "PtyStartInfo", "FileName", "Size", "PtySize", "WriteInputAsync", "Resize", "DisposeAsync", "ReadOutputAsync", "Data", "InputMode", "Echo", "Canonical", "ExitStatus"]);
const OUTPUT_ROWS_BEFORE_SUMMARY = 4;
// With Autoplay on, the end of a scene stays on screen for this long before the next scene starts.
const NEXT_SCENE_DELAY_MS = 2000;

// The largest terminal the scenes resize to. A recording that goes beyond it widens the desk for that scene only.
// The desk fits its column while the terminal text stays at least minTextPx high on screen. In a narrower column,
// as on a phone, the desk keeps that scale and scrolls sideways inside its own frame. 80 columns cannot fit a phone
// at a size people can read, and the page itself must not scroll sideways.
const DESK = { cols: 100, rows: 28, margin: 16, minTextPx: 9.5 };

const ICONS = {
  play: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2.2v11.6L13.5 8z"/></svg>',
  pause: '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 2.5h3.2v11H3.5zM9.3 2.5h3.2v11H9.3z"/></svg>',
};

const $ = (id) => document.getElementById(id);
const decoder = new TextDecoder("utf-8");

const state = {
  scenes: [],
  envs: [],
  env: null,
  lastEnv: new Map(), // the recording last shown for each OS, so a switch back to Windows keeps its ConPTY
  scene: null,
  rec: null,
  recs: new Map(),
  pos: 0,
  next: 0,
  gate: Infinity,     // index of the first event playback may not apply yet
  playing: false,
  waiting: false,     // stopped before a user action, waiting for the play button
  speed: 1,
  autoplay: false,
  advance: null,      // with Autoplay on, the wait before the next scene: { left: ms, wall: last frame time }
  lastWall: 0,
  entries: [],        // the MiniPty log, one entry per row
  outputRun: 0,
  secret: false,      // inside a step that types a password
  mode: undefined,    // the latest InputMode event
  counters: null,
  selected: null,     // the log entry shown in the detail box
  hotTimers: new Map(),
  heldTag: null,      // the code lines that stay lit while playback is stopped
};

let term = null;
let metrics = null;   // the terminal's cell size and window frame, in unscaled CSS pixels

main();

async function main() {
  let index;
  try {
    const response = await fetch("recordings/index.json", { cache: "no-cache" });
    if (!response.ok)
      throw new Error(String(response.status));
    index = await response.json();
  } catch {
    showEmpty();
    return;
  }

  state.scenes = index.scenes;
  state.envs = index.environments.filter((env) => env.scenes?.length > 0);
  if (state.envs.length === 0) {
    showEmpty();
    return;
  }

  createTerminal();
  measureTerminal();
  // layoutDesk can change the desk's height. It runs in the next frame, not in the observer's callback, so the
  // change does not count as a resize loop.
  new ResizeObserver(() => requestAnimationFrame(layoutDesk)).observe($("desk"));
  document.fonts?.ready.then(() => {
    measureTerminal();
    layoutDesk();
  });
  renderCode();
  wireControls();

  const hash = new URLSearchParams(location.hash.slice(1));
  state.env = state.envs.find((e) => e.id === hash.get("env")) ?? state.envs[0];
  const scene = state.scenes.find((s) => s.id === hash.get("scene")) ?? state.scenes[0];
  await selectScene(scene.id);
  requestAnimationFrame(frame);
}

function createTerminal() {
  term = new Terminal({
    cols: 80,
    rows: 22,
    fontSize: 12.5,
    lineHeight: 1.12,
    fontFamily: 'ui-monospace, "Cascadia Mono", "SF Mono", Menlo, Consolas, monospace',
    cursorBlink: false,
    disableStdin: true,
    scrollback: 1000,
    theme: { background: "#0f1216", foreground: "#d7dce2", cursor: "#d7dce2", selectionBackground: "#2f4f7f" },
  });
  term.open($("terminal"));
}

// The cell size and the window frame around the text area, read from the layout. Transforms do not change offset
// sizes, so this works at any desk scale.
function measureTerminal() {
  const screen = document.querySelector("#terminal .xterm-screen");
  const win = $("window");
  if (!screen || screen.offsetWidth === 0)
    return;
  metrics = {
    cellWidth: screen.offsetWidth / term.cols,
    cellHeight: screen.offsetHeight / term.rows,
    frameWidth: win.offsetWidth - screen.offsetWidth,
    frameHeight: win.offsetHeight - screen.offsetHeight,
  };
}

// Sizes the desk for the largest terminal and scales it to the column. When the column is wide enough, the desk's
// height follows from its aspect ratio. When it is not, the desk keeps the smallest readable scale, puts the window
// at its left edge, and scrolls sideways.
function layoutDesk() {
  if (!metrics)
    return;
  const desk = $("desk");
  const inner = $("desk-inner");
  let cols = DESK.cols;
  let rows = DESK.rows;
  if (state.rec) {
    cols = Math.max(cols, state.rec.maxCols);
    rows = Math.max(rows, state.rec.maxRows);
  }

  const width = Math.ceil(metrics.frameWidth + metrics.cellWidth * cols + DESK.margin * 2);
  const height = Math.ceil(metrics.frameHeight + metrics.cellHeight * rows + DESK.margin * 2);
  const fit = desk.clientWidth / width;
  const scale = Math.max(fit, DESK.minTextPx / term.options.fontSize);
  const zoomed = scale > fit;
  desk.closest(".left").classList.toggle("zoomed", zoomed);
  desk.style.aspectRatio = zoomed ? "auto" : `${width} / ${height}`;
  desk.style.height = zoomed ? `${Math.ceil(height * scale)}px` : "";
  inner.style.width = `${width}px`;
  inner.style.height = `${height}px`;
  inner.style.transform = `scale(${scale})`;
}

async function loadRecording(env, sceneId) {
  const key = `${env.id}/${sceneId}`;
  if (!state.recs.has(key)) {
    const response = await fetch(`recordings/${env.id}/${sceneId}.json`, { cache: "no-cache" });
    const rec = await response.json();
    for (const e of rec.events) {
      if (e.data)
        e.bytes = Uint8Array.from(atob(e.data), (c) => c.charCodeAt(0));
    }
    rec.end = (rec.events.length ? rec.events[rec.events.length - 1].t : 0) + 400;
    rec.maxCols = rec.size.cols;
    rec.maxRows = rec.size.rows;
    for (const e of rec.events) {
      if (e.type === "resize") {
        rec.maxCols = Math.max(rec.maxCols, e.cols);
        rec.maxRows = Math.max(rec.maxRows, e.rows);
      }
    }
    state.recs.set(key, rec);
  }
  return state.recs.get(key);
}

async function selectScene(sceneId) {
  state.advance = null; // a scene picked during the wait replaces the next scene
  let env = state.env;
  if (!env.scenes.includes(sceneId))
    env = state.envs.find((e) => e.scenes.includes(sceneId)) ?? env;
  state.env = env;
  state.lastEnv.set(env.os, env);
  state.scene = state.scenes.find((s) => s.id === sceneId);
  state.rec = await loadRecording(env, sceneId);
  renderScenes();
  renderEnvs();
  $("scene-summary").textContent = state.scene.summary;
  $("notice").textContent = env.os === "Windows" ? state.scene.windowsNotice : state.scene.unixNotice;
  $("term-title").textContent = env.os === "Windows" ? "Windows PowerShell" : "bash";
  $("env-line").textContent = environmentLine(env);
  document.title = `${state.scene.title} · MiniPty Playground`;
  layoutDesk();
  $("desk").scrollLeft = 0;
  renderProgressTicks();
  history.replaceState(null, "", `#${new URLSearchParams({ scene: sceneId, env: env.id })}`);
  seek(0);
  play();
}

// ---------------------------------------------------------------- playback

function events() {
  return state.rec.events;
}

function nextIndex(from, predicate) {
  const ev = events();
  for (let i = from; i < ev.length; i++) {
    if (predicate(ev[i]))
      return i;
  }
  return ev.length;
}

function stepIndexAfter(i) {
  return nextIndex(i + 1, (e) => e.type === "step");
}

// The latest user action playback has reached, or -1 before the first.
function currentStepIndex() {
  const ev = events();
  for (let i = state.next - 1; i >= 0; i--) {
    if (ev[i].type === "step")
      return i;
  }
  return -1;
}

// How many keys of the step at stepIndex have been written so far.
function keysWritten(stepIndex) {
  const ev = events();
  const end = Math.min(stepIndexAfter(stepIndex), state.next);
  let count = 0;
  for (let i = stepIndex + 1; i < end; i++) {
    if (ev[i].type === "input")
      count++;
  }
  return count;
}

function firstGate(from) {
  return state.autoplay ? Infinity : nextIndex(from, (e) => e.type === "step");
}

// xterm.js parses writes later, in order; resize and reset are immediate. Running them from a write callback keeps
// them in order with the output around them.
function termOp(fn) {
  term.write("", fn);
}

function seek(ms) {
  const rec = state.rec;
  termOp(() => {
    term.reset();
    term.resize(rec.size.cols, rec.size.rows);
  });
  setSize(rec.size.cols, rec.size.rows, false);
  state.next = 0;
  state.pos = ms;
  state.entries = [];
  state.outputRun = 0;
  state.secret = false;
  state.mode = undefined;
  state.selected = null;
  state.counters = { writes: 0, writeBytes: 0, chunks: 0, chunkBytes: 0, exit: null, disposed: false };
  state.gate = Infinity;
  state.advance = null;
  holdCode(null);
  applyUntil(ms, true);
  state.gate = firstGate(state.next);
  state.waiting = false;
  renderLog();
  renderCounters();
  renderDetail();
  renderClock();
  renderAction();
}

function applyUntil(ms, instant) {
  const ev = events();
  // state.next moves first, so code that runs inside apply sees the event as applied.
  while (state.next < ev.length && state.next < state.gate && ev[state.next].t <= ms) {
    const index = state.next++;
    apply(ev[index], index, instant);
  }
}

function apply(e, index, instant) {
  switch (e.type) {
    case "output":
      term.write(e.bytes);
      state.counters.chunks++;
      state.counters.chunkBytes += e.bytes.length;
      break;
    case "input":
      state.counters.writes++;
      state.counters.writeBytes += e.bytes.length;
      break;
    case "resize":
      termOp(() => term.resize(e.cols, e.rows));
      setSize(e.cols, e.rows, !instant);
      break;
    case "step":
      state.secret = !!e.secret;
      break;
    case "inputMode":
      state.mode = e;
      break;
    case "exit":
      state.counters.exit = state.rec.exit;
      break;
    case "dispose":
      state.counters.disposed = true;
      break;
  }

  addEntry(e, index, instant);
  if (!instant) {
    flashCode(TAG_OF[e.type]);
    renderCounters();
    if (e.type === "step" || e.type === "input")
      renderAction();
  }
}

function play() {
  if (ended())
    seek(0);
  state.waiting = false;
  state.playing = true;
  state.lastWall = performance.now();
  releaseCode();
  renderAction();
}

// Playback stops at once: frame() applies no event after this. The code keeps the line of the last event lit.
function pause() {
  state.playing = false;
  holdCode(lastTag());
  renderAction();
}

function ended() {
  return state.next >= events().length && state.pos >= state.rec.end;
}

function frame(now) {
  if (state.playing) {
    const dt = Math.min(now - state.lastWall, 100); // a tab in the background does not jump ahead
    state.lastWall = now;
    const ev = events();
    let target = Math.min(state.pos + dt * state.speed, state.rec.end);
    const gated = state.gate < ev.length;
    if (gated && target >= ev[state.gate].t)
      target = ev[state.gate].t;
    state.pos = target;
    applyUntil(target, false);

    if (gated && state.next >= state.gate && state.pos >= ev[state.gate].t) {
      state.playing = false;
      state.waiting = true;
      holdCode(lastTag());
      renderAction();
    } else if (ended()) {
      state.playing = false;
      startAdvance(now);
      holdCode(lastTag());
      renderAction();
    }

    renderClock();
  } else if (state.advance) {
    tickAdvance(now);
  }

  requestAnimationFrame(frame);
}

function sceneAfter(scene) {
  return state.scenes[state.scenes.indexOf(scene) + 1];
}

// With Autoplay on, a scene that ends starts the wait for the next scene. The last scene has none, so a tour of all
// the scenes stops there.
function startAdvance(now) {
  if (state.autoplay && sceneAfter(state.scene))
    state.advance = { left: NEXT_SCENE_DELAY_MS, wall: now };
}

// Counts the wait down by frame time, clamped as in playback. A tab in the background has no frames, so the wait
// stops there and does not skip the next scene.
function tickAdvance(now) {
  const advance = state.advance;
  const shown = Math.ceil(advance.left / 1000);
  advance.left -= Math.min(Math.max(now - advance.wall, 0), 100);
  advance.wall = now;
  if (advance.left <= 0)
    selectScene(sceneAfter(state.scene).id);
  else if (Math.ceil(advance.left / 1000) !== shown)
    renderAction();
}

// The play button at a gate: play the waiting action at the recorded pace, then stop before the next one.
function releaseStep() {
  state.gate = state.autoplay || !Number.isFinite(state.gate) ? Infinity : stepIndexAfter(state.gate);
  play();
}

// ---------------------------------------------------------------- log

function addEntry(e, index, instant) {
  const entry = describe(e, index);
  if (!entry)
    return;

  if (e.type === "output") {
    state.outputRun++;
    if (state.outputRun > OUTPUT_ROWS_BEFORE_SUMMARY) {
      const last = state.entries[state.entries.length - 1];
      if (last?.kind === "more") {
        last.count++;
        last.bytes += e.bytes.length;
        last.t = e.t;
        last.index = index;
        last.html = moreHtml(last);
        if (!instant)
          updateLastRow(last);
        return;
      }

      const more = { kind: "more", cls: "out more", arrow: "←", t: e.t, index, count: 1, bytes: e.bytes.length };
      more.html = moreHtml(more);
      pushEntry(more, instant);
      return;
    }
  } else if (e.type !== "inputMode" || state.outputRun === 0) {
    state.outputRun = 0;
  }

  pushEntry(entry, instant);

  // Said once per password rather than on every key: on Windows nothing marks the keys as secret.
  if (e.type === "step" && e.secret && state.mode?.unavailable) {
    pushEntry({
      t: e.t, index, kind: "note", cls: "mode", arrow: "◆",
      html: `<span class="api">InputMode</span> is null, so the app cannot tell that the user types a password`,
    }, instant);
  }
}

function pushEntry(entry, instant) {
  state.entries.push(entry);
  if (!instant)
    appendRow(entry);
}

function moreHtml(more) {
  return `<span class="api">ReadOutputAsync</span> <span class="size">… ${more.count} more chunks, ${formatBytes(more.bytes)}</span>`;
}

function describe(e, index) {
  const base = { t: e.t, index, kind: e.type };
  switch (e.type) {
    case "step":
      return { ...base, cls: "user", html: stepHtml(e, null) };
    case "started":
      return { ...base, cls: "event", arrow: "▶", html: `<span class="api">Pty.Start</span> <span class="size">the shell started</span>` };
    case "input": {
      const hidden = state.secret && state.mode && state.mode.echo === false && state.mode.canonical === true;
      const label = hidden ? "•" : keyLabel(e.label);
      const bytes = hidden ? "hidden: InputMode shows a password prompt" : hex(e.bytes);
      return { ...base, cls: "in", arrow: "→", html: `<span class="api">WriteInputAsync</span> ${escapeHtml(label)} <span class="size">${escapeHtml(bytes)}</span>` };
    }
    case "output":
      return { ...base, cls: "out", arrow: "←", html: `<span class="api">ReadOutputAsync</span> <span class="size">${formatBytes(e.bytes.length)}</span> ${tokensHtml(tokenize(e.bytes), false)}` };
    case "resize":
      return { ...base, cls: "event", arrow: "↔", html: `<span class="api">Resize</span>(${e.cols}, ${e.rows})` };
    case "inputMode":
      return { ...base, cls: "mode", arrow: "◆", html: `<span class="api">InputMode</span> ${escapeHtml(modeText(e))}` };
    case "dispose":
      return { ...base, cls: "event", arrow: "■", html: `<span class="api">DisposeAsync</span>()` };
    case "disposed":
      return { ...base, cls: "event", arrow: "■", html: `<span class="api">DisposeAsync</span> returned` };
    case "eof":
      return { ...base, cls: "out", arrow: "←", html: `<span class="api">ReadOutputAsync</span> <span class="size">${escapeHtml(e.label ?? "end of output")}</span>` };
    case "exit": {
      const exit = state.rec.exit;
      return { ...base, cls: "exit", arrow: "■", html: `<span class="api">ExitStatus</span> ${escapeHtml(exitText(exit))}` };
    }
    default:
      return { ...base, cls: "event", arrow: "·", html: escapeHtml(e.label ?? e.type) };
  }
}

function renderLog() {
  const log = $("log");
  log.replaceChildren();
  const fragment = document.createDocumentFragment();
  for (const entry of state.entries)
    fragment.append(rowFor(entry));
  log.append(fragment);
  markLatest();
  log.scrollTop = log.scrollHeight;
}

function rowFor(entry) {
  const li = document.createElement("li");
  li.className = entry.cls;
  if (entry.kind === "step") {
    li.innerHTML = entry.html;
  } else {
    li.innerHTML = `<span class="t">${formatMs(entry.t)}</span><span class="arrow">${entry.arrow}</span><span class="body">${entry.html}</span>`;
  }
  // A row shows its bytes in the detail box and lights the code that handles its event. A user action has no code.
  li.addEventListener("click", () => {
    state.selected = entry;
    renderDetail();
    holdCode(entry.kind === "step" || entry.kind === "note" ? null : TAG_OF[events()[entry.index]?.type]);
  });
  entry.row = li;
  return li;
}

function appendRow(entry) {
  const log = $("log");
  log.append(rowFor(entry));
  markLatest();
  log.scrollTop = log.scrollHeight;
  if (!state.selected)
    renderDetail();
}

function updateLastRow(entry) {
  if (entry.row)
    entry.row.querySelector(".body").innerHTML = entry.html;
  entry.row?.querySelector(".t") && (entry.row.querySelector(".t").textContent = formatMs(entry.t));
  if (!state.selected)
    renderDetail();
}

function markLatest() {
  const log = $("log");
  log.querySelector("li.latest")?.classList.remove("latest");
  log.lastElementChild?.classList.add("latest");
}

function renderDetail() {
  const box = $("detail");
  const ev = events();
  let entry = state.selected;
  if (!entry) {
    for (let i = state.entries.length - 1; i >= 0; i--) {
      if (state.entries[i].kind === "output" || state.entries[i].kind === "more") {
        entry = state.entries[i];
        break;
      }
    }
  }

  if (!entry) {
    box.innerHTML = '<p class="hint">Click a row in the MiniPty log to see its bytes and the names of its control sequences.</p>';
    return;
  }

  const e = ev[entry.index];
  if (!e.bytes) {
    box.innerHTML = `<h3>${formatMs(e.t)} ms</h3><p class="hint">${entry.html}</p>`;
    return;
  }

  const hidden = entry.kind === "input" && entry.html.includes("hidden:");
  const title = `${e.type === "input" ? "WriteInputAsync" : "ReadOutputAsync"} · ${formatBytes(e.bytes.length)} at ${formatMs(e.t)} ms`;
  const body = hidden ? '<p class="hint">Hidden, because InputMode shows a password prompt.</p>' : `<pre class="tokens">${tokensHtml(tokenize(e.bytes), true)}</pre>`;
  box.innerHTML = `<h3>${escapeHtml(title)}</h3>${body}`;
}

// ---------------------------------------------------------------- rendering

// The scene bar above the terminal. The scenes are a sequence, not a menu: the page opens on the first one, the
// bar shows where the reader is, and the arrows and the list move to another scene.
function renderScenes() {
  const select = $("scene-select");
  if (select.options.length !== state.scenes.length) {
    select.replaceChildren();
    for (const scene of state.scenes)
      select.append(new Option(scene.title, scene.id));
  }
  const index = state.scenes.indexOf(state.scene);
  select.value = state.scene.id;
  $("scene-count").textContent = `${index + 1} / ${state.scenes.length}`;
  $("prev-scene").disabled = index <= 0;
  $("next-scene").disabled = index >= state.scenes.length - 1;
}

function stepScene(delta) {
  const scene = state.scenes[state.scenes.indexOf(state.scene) + delta];
  if (scene)
    selectScene(scene.id);
}

// "Recorded on" under the terminal: one button per OS. An OS with more than one recording, such as Windows with the
// in-box and the out-of-band ConPTY, gets a second group with a button for each.
function renderEnvs() {
  const osSeg = $("os-seg");
  osSeg.replaceChildren();
  const oses = [];
  for (const env of state.envs) {
    if (!oses.includes(env.os))
      oses.push(env.os);
  }
  for (const os of oses)
    osSeg.append(segButton(os, os === state.env.os, () => selectEnv(os)));

  const variants = state.envs.filter((e) => e.os === state.env.os);
  const sameArch = variants.every((e) => e.architecture === variants[0].architecture);
  const show = variants.length > 1;
  const variantSeg = $("variant-seg");
  variantSeg.replaceChildren();
  $("variant").hidden = !show;
  if (!show)
    return;
  $("variant-label").textContent = sameArch ? "ConPTY" : "Build";
  variantSeg.setAttribute("aria-label", $("variant-label").textContent);
  for (const env of variants) {
    const name = sameArch ? (env.conPty?.kind ?? env.id) : [env.architecture, env.conPty?.kind].filter(Boolean).join(" · ");
    variantSeg.append(segButton(name, env === state.env, () => {
      state.env = env;
      selectScene(state.scene.id);
    }));
  }
}

// Switches to an OS, with the recording last shown for it, or else its first one that has this scene.
function selectEnv(os) {
  const candidates = state.envs.filter((e) => e.os === os);
  const last = state.lastEnv.get(os);
  state.env = last?.scenes.includes(state.scene.id) ? last : candidates.find((e) => e.scenes.includes(state.scene.id)) ?? candidates[0];
  selectScene(state.scene.id);
}

function segButton(label, on, onClick) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  button.classList.toggle("on", on);
  button.setAttribute("aria-pressed", String(on));
  button.addEventListener("click", onClick);
  return button;
}

function renderCounters() {
  const c = state.counters;
  $("c-writes").textContent = String(c.writes);
  $("c-writes-bytes").textContent = formatBytes(c.writeBytes);
  $("c-chunks").textContent = String(c.chunks);
  $("c-chunks-bytes").textContent = formatBytes(c.chunkBytes);
  const mode = state.mode;
  $("c-mode").textContent = !mode ? "—" : mode.unavailable ? "null" : `Echo ${mode.echo ? "on" : "off"}`;
  $("c-mode-sub").textContent = !mode ? "" : mode.unavailable ? "not reported" : mode.canonical ? "line input on" : "line input off";
  $("c-exit").textContent = c.exit ? `${c.exit.exitCode}` : c.disposed ? "null" : "running";
  $("c-exit-sub").textContent = c.exit
    ? (c.exit.signal == null ? "Signal null" : signalName(c.exit.signal))
    : c.disposed && state.env.os === "Windows" ? "never reported after DisposeAsync" : "";
}

// The bar above the terminal: the user action that plays next, or the one playing now, and the play button.
function renderAction() {
  const box = $("action");
  const kicker = $("action-kicker");
  const what = $("action-what");
  const button = $("play");
  box.classList.toggle("waiting", state.waiting);

  if (ended()) {
    const nextScene = sceneAfter(state.scene);
    kicker.textContent = "Done";
    what.textContent = nextScene ? `Next scene: ${nextScene.title}` : "This was the last scene.";
    // During the Autoplay wait, the button shows the seconds left and goes to the next scene at once. The label is
    // short, so that it fits beside the scene name on a phone.
    const label = !nextScene ? "Replay" : state.advance ? `Next in ${Math.ceil(state.advance.left / 1000)} s` : "Next scene";
    setPlayButton("play", label);
    button.onclick = () => (nextScene ? selectScene(nextScene.id) : (seek(0), play()));
    return;
  }

  if (state.waiting) {
    kicker.textContent = "Next";
    what.innerHTML = stepHtml(events()[state.gate], null);
    setPlayButton("play", "Play");
    button.onclick = releaseStep;
    return;
  }

  const stepIndex = currentStepIndex();
  if (stepIndex < 0) {
    kicker.textContent = state.playing ? "Starting" : "Paused";
    what.textContent = "The shell starts in the PTY…";
  } else {
    kicker.textContent = state.playing ? "Now" : "Paused";
    what.innerHTML = stepHtml(events()[stepIndex], keysWritten(stepIndex));
  }

  setPlayButton(state.playing ? "pause" : "play", state.playing ? "Pause" : "Resume");
  button.onclick = state.playing ? pause : play;
}

// Replaces the icon and the label only when they change. Chrome drops a click when the element under the pointer is
// replaced between the press and the release. renderAction runs on each key, so a Pause click during typing got lost,
// and playback went on until the next click. The CSS also sends every click to the button itself.
function setPlayButton(icon, label) {
  const button = $("play");
  if (button.dataset.icon === icon && button.dataset.label === label)
    return;
  button.dataset.icon = icon;
  button.dataset.label = label;
  button.innerHTML = `${ICONS[icon]}<span>${escapeHtml(label)}</span>`;
}

// Describes a user action from its recorded fields. written is how many of its keys have been written, which dims
// the rest of a typed command, or null to show the action without progress.
function stepHtml(step, written) {
  switch (step.action) {
    case "type": {
      const chars = Array.from(step.text ?? "");
      const done = written == null ? chars.length : Math.min(written, chars.length);
      const rest = chars.slice(done).join("");
      const chip = `<code class="chip">${escapeHtml(chars.slice(0, done).join(""))}${rest ? `<span class="rest">${escapeHtml(rest)}</span>` : ""}</code>`;
      const enter = step.enter ? ` ${keyHtml("⏎ Enter", written != null && written <= chars.length)}` : "";
      return `The user types ${step.secret ? "a password " : ""}${chip}${enter}`;
    }
    case "key":
      return `The user presses ${keyHtml(step.key, written === 0)}`;
    case "resize":
      return `The user resizes the window to <code class="chip">${step.cols} × ${step.rows}</code>`;
    case "close":
      return "The user closes the tab";
    default:
      return escapeHtml(step.label ?? step.action);
  }
}

function stepText(step) {
  switch (step.action) {
    case "type":
      return `The user types ${step.secret ? "a password" : step.text}${step.enter ? " and presses Enter" : ""}`;
    case "key":
      return `The user presses ${step.key}`;
    case "resize":
      return `The user resizes the window to ${step.cols} × ${step.rows}`;
    case "close":
      return "The user closes the tab";
    default:
      return step.label ?? step.action;
  }
}

// A key cap; dim marks a key not pressed yet while the action plays.
function keyHtml(text, dim) {
  return `<kbd class="key${dim ? " rest" : ""}">${escapeHtml(text)}</kbd>`;
}

function renderClock() {
  $("clock").textContent = `${formatMs(state.pos)} ms`;
  $("progress-fill").style.width = `${Math.min(100, (state.pos / state.rec.end) * 100)}%`;
}

function renderProgressTicks() {
  const bar = $("progress");
  bar.querySelectorAll(".tick").forEach((t) => t.remove());
  for (const e of state.rec.events) {
    if (e.type !== "step")
      continue;
    const tick = document.createElement("span");
    tick.className = "tick";
    tick.style.left = `${(e.t / state.rec.end) * 100}%`;
    tick.title = stepText(e);
    bar.append(tick);
  }
}

function setSize(cols, rows, flash) {
  $("term-size").textContent = `${cols} × ${rows}`;
  if (flash) {
    const win = $("window");
    win.classList.add("flash");
    setTimeout(() => win.classList.remove("flash"), 60);
  }
}

function renderCode() {
  const pre = $("code");
  pre.replaceChildren();
  for (const [text, tag] of CODE) {
    const line = document.createElement("span");
    line.className = "line" + (tag ? " tagged" : "");
    if (tag)
      line.dataset.tag = tag;
    line.innerHTML = highlightLine(text) || " ";
    pre.append(line);
  }
}

function codeLines(tag) {
  return document.querySelectorAll(`#code .line[data-tag="${tag}"]`);
}

// During playback, the lines that handle an event light up for a moment.
function flashCode(tag) {
  if (!tag)
    return;
  releaseCode();
  for (const line of codeLines(tag))
    line.classList.add("hot");
  clearTimeout(state.hotTimers.get(tag));
  state.hotTimers.set(tag, setTimeout(() => {
    for (const line of codeLines(tag))
      line.classList.remove("hot");
  }, 120));
}

// While playback is stopped (Pause, a step that waits, or the end of the scene), the lines of one event stay lit, so
// the terminal, the log, and the code show the same moment. null turns off all the lines.
function holdCode(tag) {
  for (const timer of state.hotTimers.values())
    clearTimeout(timer);
  state.hotTimers.clear();
  for (const line of document.querySelectorAll("#code .line.hot"))
    line.classList.remove("hot");
  state.heldTag = tag ?? null;
  if (tag) {
    for (const line of codeLines(tag))
      line.classList.add("hot");
  }
}

function releaseCode() {
  if (state.heldTag == null)
    return;
  for (const line of codeLines(state.heldTag))
    line.classList.remove("hot");
  state.heldTag = null;
}

// The code tag of the last applied event, the latest row in the log. A user action has no code, so it gives null.
function lastTag() {
  return TAG_OF[events()[state.next - 1]?.type] ?? null;
}

function environmentLine(env) {
  const parts = [`Recorded on ${env.osDescription}`, env.framework, `MiniPty ${env.miniPty}`];
  if (env.conPty)
    parts.push(`${env.conPty.kind} ConPTY ${env.conPty.version}`);
  parts.push(env.recordedAt);
  return parts.join(" · ");
}

function showEmpty() {
  $("empty").hidden = false;
  document.querySelector(".stage").hidden = true;
  document.querySelector(".below").hidden = true;
}

// ---------------------------------------------------------------- controls

function wireControls() {
  $("scene-select").addEventListener("change", (event) => selectScene(event.target.value));
  $("prev-scene").addEventListener("click", () => stepScene(-1));
  $("next-scene").addEventListener("click", () => stepScene(1));

  // The "Scroll sideways" hint on a phone goes away once the reader scrolls the desk.
  const desk = $("desk");
  desk.addEventListener("scroll", () => desk.closest(".left").classList.toggle("scrolled", desk.scrollLeft > 0), { passive: true });

  // A link in "Why MiniPty" plays the scene that shows the feature. data-unix asks for a Linux or macOS recording,
  // because InputMode is null on Windows.
  for (const link of document.querySelectorAll("a[data-scene]"))
    link.addEventListener("click", (event) => playSceneFromLink(event, link));

  $("restart").addEventListener("click", () => {
    seek(0);
    play();
  });

  $("autoplay").addEventListener("change", (event) => {
    state.autoplay = event.target.checked;
    if (state.autoplay) {
      state.gate = Infinity;
      if (state.waiting)
        play();
      else if (ended())
        startAdvance(performance.now());
    } else {
      // Turning Autoplay off during the wait keeps the reader on this scene.
      state.gate = firstGate(state.next);
      state.advance = null;
    }
    renderAction();
  });

  for (const button of document.querySelectorAll("[data-speed]")) {
    button.addEventListener("click", () => {
      state.speed = Number(button.dataset.speed);
      for (const other of document.querySelectorAll("[data-speed]"))
        other.classList.toggle("on", other === button);
    });
  }

  $("progress").addEventListener("click", (event) => {
    const rect = event.currentTarget.getBoundingClientRect();
    seek(((event.clientX - rect.left) / rect.width) * state.rec.end);
    play();
  });

  // Space presses the play button, as in a media player. Inside a control, the control keeps its own keys.
  document.addEventListener("keydown", (event) => {
    if (event.key !== " " || event.ctrlKey || event.altKey || event.metaKey)
      return;
    if (event.target instanceof HTMLElement && event.target.closest("button, input, select, textarea, a"))
      return;
    event.preventDefault();
    $("play").click();
  });
}

async function playSceneFromLink(event, link) {
  event.preventDefault();
  const sceneId = link.dataset.scene;
  if (link.dataset.unix != null && state.env.os === "Windows") {
    const has = (env) => env && env.os !== "Windows" && env.scenes.includes(sceneId);
    const unix = state.envs.find(has);
    if (unix)
      state.env = has(state.lastEnv.get(unix.os)) ? state.lastEnv.get(unix.os) : unix;
  }
  await selectScene(sceneId);
  const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
  document.querySelector(".scene-bar").scrollIntoView({ behavior: reduce ? "auto" : "smooth", block: "start" });
}

// ---------------------------------------------------------------- bytes

// Splits decoded output into text and named control sequences. A sequence split across two chunks shows as two
// partial tokens, as the terminal received it.
function tokenize(bytes) {
  const s = decoder.decode(bytes);
  const out = [];
  let text = "";
  const flush = () => {
    if (text) {
      out.push({ kind: "text", value: text });
      text = "";
    }
  };

  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c === 0x1b) {
      flush();
      const n = s[i + 1];
      if (n === "[") {
        let j = i + 2;
        while (j < s.length && (s.charCodeAt(j) < 0x40 || s.charCodeAt(j) > 0x7e))
          j++;
        const body = s.slice(i + 2, j);
        const final = s[j] ?? "";
        out.push({ kind: "csi", value: `ESC[${body}${final}`, title: describeCsi(body, final) });
        i = j;
      } else if (n === "]") {
        let stop = s.length;
        let stopLength = 0;
        for (let j = i + 2; j < s.length; j++) {
          if (s[j] === "\x07") {
            stop = j;
            stopLength = 1;
            break;
          }
          if (s[j] === "\x1b" && s[j + 1] === "\\") {
            stop = j;
            stopLength = 2;
            break;
          }
        }
        const body = s.slice(i + 2, stop);
        out.push({ kind: "osc", value: `ESC]${body}`, title: describeOsc(body) });
        i = stop + stopLength - 1;
      } else {
        out.push({ kind: "esc", value: `ESC${n ?? ""}`, title: "escape sequence" });
        i++;
      }
      continue;
    }

    if (c === 0x0d) {
      flush();
      out.push({ kind: "ctl", value: "\\r", title: "carriage return" });
    } else if (c === 0x0a) {
      flush();
      out.push({ kind: "ctl", value: "\\n", title: "line feed", newline: true });
    } else if (c === 0x09) {
      flush();
      out.push({ kind: "ctl", value: "\\t", title: "tab" });
    } else if (c === 0x08) {
      flush();
      out.push({ kind: "ctl", value: "\\b", title: "backspace: cursor one column left" });
    } else if (c === 0x07) {
      flush();
      out.push({ kind: "ctl", value: "BEL", title: "bell" });
    } else if (c < 0x20 || c === 0x7f) {
      flush();
      out.push({ kind: "ctl", value: c === 0x7f ? "DEL" : `^${String.fromCharCode(c + 64)}`, title: `control character 0x${c.toString(16).padStart(2, "0")}` });
    } else {
      text += s[i];
    }
  }

  flush();
  return out;
}

function describeCsi(body, final) {
  const prefix = /^[?>=<]/.test(body) ? body[0] : "";
  const params = prefix ? body.slice(1) : body;
  if (prefix === "?" && params === "25" && (final === "h" || final === "l"))
    return final === "h" ? "show the cursor" : "hide the cursor";
  if (prefix === "?" && (final === "h" || final === "l")) {
    const names = params.split(";").map((p) => PRIVATE_MODES[p] ?? `private mode ${p}`);
    return `${final === "h" ? "enable" : "disable"} ${names.join(", ")}`;
  }
  if (final === "t" && params.startsWith("8;")) {
    const [, rows, cols] = params.split(";");
    return `set the text area to ${cols}x${rows}`;
  }
  if (final === "n" && params === "6")
    return "cursor position query";
  if (final === "J" && params === "2")
    return "erase the whole display";
  if (final === "K" && (params === "" || params === "0"))
    return "erase to the end of the line";
  if (final === "H" && params === "")
    return "cursor to the top left";
  if (final === "m" && (params === "" || params === "0"))
    return "SGR: reset colors and style";
  return CSI_FINALS[final] ?? "control sequence";
}

function describeOsc(body) {
  const number = body.split(";")[0];
  if (number === "0" || number === "2")
    return "window title";
  if (number === "8")
    return "hyperlink";
  return `operating system command ${number}`;
}

function tokensHtml(tokens, full) {
  let html = "";
  for (const token of tokens) {
    if (token.kind === "text") {
      html += escapeHtml(token.value);
      continue;
    }
    html += `<span class="tok-${token.kind}" title="${escapeHtml(token.title ?? "")}">${escapeHtml(token.value)}</span>`;
    if (full && token.newline)
      html += "\n";
  }
  return html;
}

// ---------------------------------------------------------------- helpers

function keyLabel(label) {
  if (label === "Space")
    return "␣ Space";
  if (label === "Enter")
    return "⏎ Enter";
  return label ?? "";
}

function hex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).toUpperCase().padStart(2, "0")).join(" ");
}

function modeText(e) {
  if (e.unavailable)
    return "null: not reported (on Windows, or after the shell exits)";
  const text = `Echo ${e.echo ? "on" : "off"}, line input ${e.canonical ? "on" : "off"}`;
  if (!e.echo && e.canonical)
    return `${text} → a password prompt`;
  if (!e.echo && !e.canonical)
    return `${text} → the program reads each key and draws it itself`;
  return text;
}

function exitText(exit) {
  if (!exit)
    return "not reported";
  return `${exit.exitCode}${exit.signal == null ? "" : ` (${signalName(exit.signal)})`}`;
}

function signalName(signal) {
  const table = state.env.os === "Linux" ? SIGNALS_LINUX : SIGNALS_BSD;
  return table[signal] ? `${table[signal]}, signal ${signal}` : `signal ${signal}`;
}

function highlightLine(line) {
  let out = "";
  let i = 0;
  while (i < line.length) {
    if (line.startsWith("//", i)) {
      out += `<span class="c">${escapeHtml(line.slice(i))}</span>`;
      break;
    }
    if (line[i] === '"') {
      let j = i + 1;
      while (j < line.length && line[j] !== '"')
        j += line[j] === "\\" ? 2 : 1;
      out += `<span class="s">${escapeHtml(line.slice(i, j + 1))}</span>`;
      i = j + 1;
      continue;
    }
    const ui = /^terminal(\.\w+)?/.exec(line.slice(i));
    if (ui) {
      out += `<span class="ui">${escapeHtml(ui[0])}</span>`;
      i += ui[0].length;
      continue;
    }
    const word = /^[A-Za-z_]\w*/.exec(line.slice(i));
    if (word) {
      out += KEYWORDS.has(word[0]) ? `<span class="k">${word[0]}</span>`
        : API_NAMES.has(word[0]) ? `<span class="api">${word[0]}</span>`
        : escapeHtml(word[0]);
      i += word[0].length;
      continue;
    }
    out += escapeHtml(line[i]);
    i++;
  }
  return out;
}

function formatMs(ms) {
  return ms.toLocaleString("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

function formatBytes(n) {
  if (n < 1024)
    return `${n} B`;
  return `${(n / 1024).toLocaleString("en-US", { maximumFractionDigits: 1 })} KiB`;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}
