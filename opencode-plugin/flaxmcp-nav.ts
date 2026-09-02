// flaxmcp-nav.ts — OpenCode plugin for the flaxmcp-nav daemon.
//
// TRANSPORT (2026-07-17 rewrite): tool calls talk to the daemon DIRECTLY over
// the named pipe (\\.\pipe\flaxmcp-nav, newline-delimited JSON) via node:net.
// The previous implementation spawned flaxmcp-nav.exe TWICE per tool call
// (a --health ping + the client exe) — ~300-400ms of process-spawn overhead
// on every nav query. Direct pipe I/O is single-digit ms. The exe is only
// used to SPAWN the daemon when the pipe is down.
//
// LIFECYCLE (2026-07-17): the daemon is a shared, machine-wide sidecar used
// by every concurrent agent session. This plugin deliberately does NOT shut
// it down when a session ends — the old on-exit `--shutdown` killed the warm
// daemon out from under every other session (and any in-flight calls) each
// time one agent finished. Operator kill switches: `daemon.ps1 start -Kill`
// or the desktop flaxmcp-nav-kill.bat.
//
// ERROR CONTRACT: the daemon answers on stdout/pipe with ONE JSON line for
// BOTH success (ok:true) and in-band errors (ok:false). Any parseable JSON
// response is returned verbatim; only a dead pipe is an IPC failure worth a
// respawn. (Pre-2026-07-17 the exit-code-based check turned every arg error
// into a bogus daemon_unreachable + duplicate-daemon spawn storm.)
//
// DIVISION OF LABOR (the honest "what's fastest for what"):
//   - plain TEXT in files          -> built-in `grep` tool   (fastest; keep)
//   - files BY NAME                -> built-in `glob` tool   (fastest; keep)
//   - read a file                  -> built-in `read` tool   (keep)
//   - SEMANTIC C# (refs/callers/   -> THIS tool (csharp/* atomics; grep can't
//     impls)                          do semantic analysis)
//   - ENGINE API doc lookup        -> THIS tool (flax_api/* atomics)
//   - doc capability/composes      -> THIS tool (docs/* + discovery atomics)
//
// Plugin does NOT replace grep/glob/read — only the SEMANTIC daemon path.

import { tool, type Plugin } from "@opencode-ai/plugin";
import path from "path";
import fs from "fs";
import net from "net";
import { execFile, spawn } from "child_process";
// Shared verification proof-signal matcher, single source of truth with
// cs-edit-gates.mjs (api-verify-gate.mjs + reuse-verify-gate.mjs merged there
// 2026-08-04). opencode loads this .ts transpile-only (no tsc/build step), so
// importing the .mjs directly is fine — the two gates must accept the SAME
// signals (flaxnav, nav/query, flax_api/*, csharp/* — either as the tool name
// or as the inner name of a flax_call / flax_flax_call call) or the documented
// subagent path (nav/query via flax_call) stays blocked here while
// cs-edit-gates already accepts it.
// @ts-ignore — .mjs has no declarations; transpile-only loading, not type-checked
// From lib/, not from the plugin entry point: cs-edit-gates.mjs may export
// nothing but `default` (OpenCode calls every named export of a registered
// plugin as a factory), so all shared predicates live in the .core module.
import { isVerificationCall } from "./lib/cs-edit-gates.core.mjs";
// @ts-ignore — dependency-free shared state; OpenCode transpiles this file
// without type-checking and the core module has no declarations.
import { callFailed } from "./lib/gate-shared.core.mjs";
// @ts-ignore — dependency-free shared state; OpenCode transpiles this file
// without type-checking and the core module has no declarations.
import {
  createNavState,
  hasEditCredit,
  MAX_EDITS_PER_NAV,
  recordSuccessfulEdit,
  resetNavState,
  setNavGateProblem,
} from "./lib/flaxmcp-nav-gate.core.mjs";
// Count:0 terminal-note gate, extracted to a dependency-free shared helper so
// the drift test (tests/flaxmcp-nav.drift-test.ts, bare `tsx`, no opencode
// runtime types) can import it without resolving @opencode-ai/plugin. Same
// @ts-ignore reasoning as the cs-edit-gates import above.
// @ts-ignore — .mjs has no declarations; transpile-only loading, not type-checked
import { injectCountZeroTerminalNote } from "./lib/flaxmcp-tool-aliases.core.mjs";

// ─── Agent-safe atomics list (must match the daemon fixture) ────────────────
const ATOMICS = [
  // Status/control reads. Administrative shutdown and campaign controls stay
  // daemon-local: any agent can call this tool, while the daemon is shared by
  // every active session on the machine.
  "__ping__", "__status__", "__health__", "__help__", "__warmup__",
  // Roslyn (C# semantic)
  "csharp/find_definition", "csharp/find_references", "csharp/symbol_search",
  "csharp/find_implementations", "csharp/get_call_hierarchy",
  "csharp/grep_symbol_context", "csharp/describe_symbol", "csharp/find_related_symbols",
  "csharp/list_plugin_tools", "csharp/index_health",
  // docs + discovery
  "docs/find_section", "docs/find_doc", "docs/grep", "docs/find_capability", "docs/find_composes",
  // engine API
  "flax_api/lookup", "flax_api/search", "flax_api/members_of", "flax_api/enum_values",
  "flax_api/inheritance_chain",
  // atlas
  "atlas/diff_tree",
  // receipt queries + plugin catalog
  "receipt/search", "receipt/recent", "receipt/by_id", "plugin/catalog",
] as const;
type Atomic = (typeof ATOMICS)[number];

// ─── Pipe transport ─────────────────────────────────────────────────────────
const PIPE_PATH = "\\\\.\\pipe\\flaxmcp-nav";
const CALL_TIMEOUT_MS = 60_000;
const PING_TIMEOUT_MS = 3_000;
const START_TIMEOUT_MS = 30_000;
const OUTPUT_CAP = 32_000;

function delay(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

/** One request/response over the daemon's named pipe. Resolves the raw JSON
 * line, or null when the pipe is unreachable / gave no response in time. */
function pipeCall(payload: Record<string, unknown>, timeoutMs = CALL_TIMEOUT_MS): Promise<string | null> {
  return new Promise((resolve) => {
    let done = false;
    let buf = "";
    const sock = net.connect(PIPE_PATH);
    const finish = (v: string | null) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { sock.destroy(); } catch { /* already closed */ }
      resolve(v);
    };
    const timer = setTimeout(() => finish(null), timeoutMs);
    sock.on("connect", () => {
      sock.write(JSON.stringify(payload) + "\n");
    });
    sock.on("data", (d) => {
      buf += d.toString("utf-8");
      const nl = buf.indexOf("\n");
      if (nl >= 0) finish(buf.slice(0, nl).trim());
    });
    sock.on("error", () => finish(null));
    sock.on("close", () => finish(buf.trim().length > 0 ? buf.trim() : null));
  });
}

async function pipePing(): Promise<boolean> {
  const out = await pipeCall({ atomic: "__ping__" }, PING_TIMEOUT_MS);
  if (!out) return false;
  try {
    const r = JSON.parse(out);
    return r.ok === true && r.pong === true;
  } catch {
    return false;
  }
}

/** Mirror of the exe client's CapOutput: keep responses under OUTPUT_CAP by
 * trimming trailing items from the first result array, flagging truncation. */
function capOutput(line: string): string {
  if (line.length <= OUTPUT_CAP) return line;
  const note = `response was ${line.length} bytes — over the ${OUTPUT_CAP}-byte cap. Narrow the query.`;
  try {
    const root = JSON.parse(line) as Record<string, unknown>;
    const candidateKeys = ["results", "hits", "tools", "values", "unmapped", "units", "countDrift"];
    const key = candidateKeys.find((k) => Array.isArray(root[k]));
    if (!key) return JSON.stringify({ ok: false, errorCode: "response_truncation_failed", error: note, retryable: false });
    const arr = root[key] as unknown[];
    const originalCount = arr.length;
    let excess = line.length - OUTPUT_CAP;
    while (arr.length > 0 && excess > 0) {
      const item = arr.pop();
      excess -= JSON.stringify(item).length + 1;
    }
    root.truncated = true;
    root.originalCount = originalCount;
    root.returnedCount = arr.length;
    return JSON.stringify(root);
  } catch {
    return JSON.stringify({ ok: false, errorCode: "malformed_response", error: "The nav daemon returned malformed JSON.", retryable: true });
  }
}

// ─── Daemon lifecycle: spawn + stale-process recovery (exe used ONLY here) ──
type SpawnResult = { ok: true; info?: string } | { ok: false; info: string };

let daemonStartPromise: Promise<SpawnResult> | null = null;

function isWin() { return process.platform === "win32"; }

function findExe(root: string): string {
  const name = "flaxmcp-nav" + (isWin() ? ".exe" : "");
  const debug = path.join(root, "tools", "flaxmcp-nav", "bin", "Debug", "net8.0", name);
  const release = path.join(root, "tools", "flaxmcp-nav", "bin", "Release", "net8.0", name);
  const candidates = [debug, release].filter((candidate) => fs.existsSync(candidate));
  if (candidates.length > 0) {
    candidates.sort((a, b) => {
      const time = fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs;
      if (time !== 0) return time;
      return b.includes(`${path.sep}Release${path.sep}`) ? -1 : 1;
    });
    return candidates[0];
  }
  // Search recursively under bin/ for any flaxmcp-nav binary.
  const binRoot = path.join(root, "tools", "flaxmcp-nav", "bin");
  if (fs.existsSync(binRoot)) {
    const hits = fs.readdirSync(binRoot, { recursive: true })
      .map(String)
      .filter(f => f.endsWith(name));
    if (hits.length) {
      hits.sort();
      return path.join(binRoot, hits[hits.length - 1]);
    }
  }
  return debug; // fall-through; caller will detect missing
}

function execOneShot(exe: string, args: string[], timeoutMs = 30_000): Promise<{ stdout: string; stderr: string; code: number }> {
  return new Promise((resolve) => {
    execFile(exe, args, { encoding: "utf-8", maxBuffer: 16 * 1024 * 1024, windowsHide: true, timeout: timeoutMs }, (err, stdout, stderr) => {
      if (err && (err as any).killed && err.message?.includes("timeout")) {
        resolve({ stdout: "", stderr: "timeout", code: 124 });
        return;
      }
      resolve({
        stdout: stdout ?? "",
        stderr: stderr ?? "",
        code: err ? (err as NodeJS.ErrnoException & { code?: number }).code === "ENOENT" ? 127 : 1 : 0,
      });
    });
  });
}

// ─── Stale-daemon process killer (Windows only) ─────────────────────────────
// Matches by process NAME + '--daemon' command line, NOT ExecutablePath: the
// daemon shadow-copies itself out of bin/ (so builds are never blocked by
// file locks), which means its ExecutablePath is a %TEMP% shadow dir, not the
// bin path this plugin resolves.
interface KillResult { killed: number; pids: number[]; }
async function killStaleDaemons(): Promise<KillResult> {
  const result: KillResult = { killed: 0, pids: [] };
  if (!isWin()) return result;
  try {
    const psCmd =
      `Get-CimInstance Win32_Process | Where-Object { ` +
      `$_.Name -eq 'flaxmcp-nav.exe' -and $_.CommandLine -like '*--daemon*' } | ` +
      `Select-Object ProcessId | ConvertTo-Json -Compress`;
    const r = await execOneShot('powershell', ['-NoProfile', '-NonInteractive', '-Command', psCmd], 10_000);
    if (r.code !== 0 || !r.stdout.trim()) return result;

    let parsed: any;
    try { parsed = JSON.parse(r.stdout.trim()); } catch { return result; }

    // ConvertTo-Json emits a bare object for one result, an array for many.
    const pids: number[] = Array.isArray(parsed)
      ? parsed.map((p: any) => p.ProcessId).filter((id: number) => id && id > 0)
      : (parsed && typeof parsed === 'object' && 'ProcessId' in parsed)
        ? [parsed.ProcessId]
        : [];

    if (pids.length === 0) return result;

    const killCmd = pids.map(id => `Stop-Process -Id ${id} -Force -ErrorAction SilentlyContinue`).join('; ');
    await execOneShot('powershell', ['-NoProfile', '-NonInteractive', '-Command', killCmd], 10_000);

    // Wait for process exit and Windows named-pipe/mutex cleanup before
    // respawning. The old instance can retain the pipe for ~2 seconds.
    await delay(2500);

    result.killed = pids.length;
    result.pids = pids;
  } catch {
    // Non-fatal — recovery will still attempt a fresh spawn.
  }
  return result;
}

// TASK-SCHEDULER SPAWN (2026-07-18): the ORIGINAL "3 subagents got stuck"
// incident happened because a naive `child_process.spawn({detached:true})`
// only gets CREATE_NEW_PROCESS_GROUP on Windows -- it does NOT reliably
// break the child out of a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_
// CLOSE set on the parent. When the calling agent's own tool-runner sits
// inside such a job, the daemon it just spawned dies the instant that
// agent's turn ends, yanking it out from under every other concurrent
// session mid-query.
//
// A process launched via `Start-ScheduledTask` is a child of the Task
// Scheduler service (schedsvc), never of whatever called it -- structurally
// outside the caller's job tree regardless of that job's policy. This is
// the standard Windows mechanism for launching something that must outlive
// its trigger. scripts/dev/install-nav-daemon-task.ps1 registers a
// triggerless 'FlaxMcp-NavDaemon' task (run scripts/dev/install-nav-daemon-
// task.ps1 once to set it up) whose action is `daemon.ps1 start` -- this
// reuses all of daemon.ps1's existing self-heal/shadow-copy/stale-build
// logic unchanged. Verified 2026-07-18: killed the daemon, triggered the
// task, confirmed the daemon PID was still alive and healthy after its own
// immediate parent (the task's pwsh.exe launcher) had already exited.
const NAV_TASK_NAME = "FlaxMcp-NavDaemon";

async function spawnViaScheduledTask(): Promise<boolean> {
  if (!isWin()) return false;
  const r = await execOneShot(
    "powershell",
    ["-NoProfile", "-NonInteractive", "-Command",
      `Start-ScheduledTask -TaskName '${NAV_TASK_NAME}' -ErrorAction Stop`],
    10_000
  );
  return r.code === 0;
}

async function spawnDaemon(exe: string): Promise<SpawnResult> {
  if (daemonStartPromise) return await daemonStartPromise;
  daemonStartPromise = (async (): Promise<SpawnResult> => {
    // Shared helper: spawn a daemon process and poll the pipe for readiness.
    // Prefers the Scheduled-Task path (job-object-safe); falls back to a
    // direct child_process.spawn only if the task isn't registered yet
    // (e.g. install-nav-daemon-task.ps1 was never run on this machine) so
    // the plugin still works, just without the job-teardown immunity.
    const attempt = async (): Promise<boolean> => {
      const viaTask = await spawnViaScheduledTask();
      if (!viaTask) {
        try {
          const proc = spawn(exe, ["--daemon"], {
            detached: true,
            stdio: "ignore",
            windowsHide: true,
          });
          // ENOENT race guard: existsSync ran earlier, but the file can
          // disappear before spawn. Without a listener the async 'error'
          // event is an unhandled-error process kill.
          proc.on("error", () => { /* pipe poll below times out harmlessly */ });
          proc.unref();
        } catch {
          return false;
        }
      }
      const deadline = Date.now() + START_TIMEOUT_MS;
      while (Date.now() < deadline) {
        await delay(300);
        if (await pipePing()) return true;
      }
      return false;
    };

    // Phase 1 — normal spawn + pipe poll.
    if (await attempt()) return { ok: true };

    // Phase 2 — verify daemon is truly dead before killing (pipe pings are cheap).
    let trulyDead = true;
    for (let i = 0; i < 3; i++) {
      await delay(2000);
      if (await pipePing()) { trulyDead = false; break; }
    }
    if (trulyDead) {
      const { killed, pids } = await killStaleDaemons();
      await delay(1000);
      if (await attempt()) {
        return { ok: true, info: `recovered; killed stale PID(s) ${pids.join(", ") || "(none)"}` };
      }
      return { ok: false, info: `spawn+ping failed; killStaleDaemons killed ${killed} process(es) [${pids.join(", ") || "none"}], re-spawn still unhealthy` };
    }
    return { ok: true, info: "daemon answered pings late — likely cold start under load" };
  })();
  const result = await daemonStartPromise;
  setTimeout(() => { daemonStartPromise = null; }, 1000);
  return result;
}

async function ensureDaemon(root: string): Promise<{ ok: boolean; exe: string; reason?: string }> {
  const exe = findExe(root);
  if (!fs.existsSync(exe)) {
    return { ok: false, exe, reason: `flaxmcp-nav binary not found at ${exe}. Build: pwsh scripts/build/build-lock.ps1 tools/flaxmcp-nav/flaxmcp-nav.csproj` };
  }
  if (await pipePing()) return { ok: true, exe };
  await delay(500);
  if (await pipePing()) return { ok: true, exe };
  const sr = await spawnDaemon(exe);
  return sr.ok ? { ok: true, exe } : { ok: false, exe, reason: sr.info };
}

/** Build the pipe request. Control verbs (__shutdown__ force=true,
 * __campaign_lock__ ownerPid/reason) read their args at the TOP level of the
 * request object; regular atomics take an `args` object plus a server-side
 * deadline slightly under our client timeout. */
function buildRequest(atomic: string, a: Record<string, unknown>): Record<string, unknown> {
  return atomic.startsWith("__")
    ? { atomic, ...a }
    : { atomic, args: a, timeoutMs: CALL_TIMEOUT_MS - 2_000 };
}

async function callAtomic(root: string, atomic: string, a: Record<string, unknown>): Promise<string> {
  const req = buildRequest(atomic, a);

  // Fast path: the daemon is usually up — one pipe round-trip, no processes.
  let out = await pipeCall(req);
  if (out === null) {
    // Pipe unreachable — spawn/recover, then retry once.
    const ensure = await ensureDaemon(root);
    if (!ensure.ok) {
      return JSON.stringify({
        ok: false,
        error: "daemon_unreachable",
        message: ensure.reason ?? "could not start daemon",
        hint: "LOUD FAILURE — HARD RULE 2 safety gate is DOWN. Fix: pwsh -NoProfile -NonInteractive -File tools/flaxmcp-nav/smoke-daemon.ps1  OR  Start-Process -FilePath 'tools/flaxmcp-nav/bin/Debug/net8.0/flaxmcp-nav.exe' -ArgumentList '--daemon' -WindowStyle Hidden  — then retry. Verify: flaxnav {atomic:'csharp/index_health'} must return ok:true. If gate blocks your .cs edit, FLAXMCP_NAV_GATE_DISABLE=1 is NOT the fix — restart the daemon.",
        remediation: "nav daemon is down — HARD RULE 2 cannot be satisfied. This is a loud failure, not a silent skip. Restart the daemon (see hint), verify with csharp/index_health, then retry your edit. Do NOT bypass HARD RULE 2 by editing without verification.",
        terminal: false,
        retryable: true,
      });
    }
    out = await pipeCall(req);
    if (out === null) {
      return JSON.stringify({
        ok: false,
        error: "daemon_unreachable",
        message: "daemon spawned but the pipe gave no response — check %TEMP%\\flaxmcp-nav\\daemon.log — then retry flaxnav {atomic:'csharp/index_health'}",
        hint: "Pipe exists but daemon is not responding — try killing stale daemon: Get-Process flaxmcp-nav | Stop-Process -Force; then restart — see above",
        terminal: false,
        retryable: true,
      });
    }
  }
  return capOutput(out);
}

// ─── flaxnav-gate: block C# edits until flaxnav has been called ───────────
// AGENTS.md/orch.md/SKILL.md all say "MANDATORY: call flaxnav before writing
// C#" — pure prose, repeated four+ times, and still routinely skipped. This
// is the actual enforcement: block edit/write/patch on .cs files until flaxnav
// has been called, and re-block after every MAX_EDITS_PER_NAV .cs edits to
// force periodic re-verification. Coarse (not per-symbol) by design — a
// session-wide "did you check at all" bar is much lower risk of wrongly
// blocking a legitimate edit than trying to track exactly which symbol was
// looked up. The edit-window counter prevents the "one trivial lookup then
// guess for the rest of the session" pattern.
//
// Module-scoped state, not per-call: this plugin factory runs once per
// server process (see daemonStartPromise above — same lifecycle), and hooks
// fire per-session via `sessionID`, so a single shared Map keyed by
// sessionID tracking { editsSinceNav } is the right primitive.
//
// FAIL-OPEN, always: this hook runs before EVERY tool call in EVERY concurrent
// session on the machine, not just the offending one. A bug in the gate's own
// logic must never brick unrelated tool calls. A deliberate nav problem is
// attached to the shared hook output; command-guards turns it into the final
// C# preflight error alongside API/reuse problems.
//
// Escape hatch: set FLAXMCP_NAV_GATE_DISABLE=1 to turn this off entirely
// (matches the daemon's existing operator-kill-switch convention).
// "patch" was aspirational — the tool OpenCode actually exposes is named
// `apply_patch`, so this set never matched it. Measured 2026-08-04: 296
// apply_patch calls in 4 days, 78 of them rewriting .cs files, every one of
// them skipping this gate. Same hole existed in the merged cs-edit-gates
// (api-verify-gate + reuse-verify-gate); all three now share FILE_EDIT_TOOLS.
const EDIT_TOOLS = new Set(["edit", "write", "patch", "apply_patch", "multiedit"]);
// Per-session edit-window gate: admission is checked before the edit, but the
// counter is committed in tool.execute.after only when the edit succeeds. A
// later edit/API/path gate must not consume nav proof and create a retry loop.
const sessionNavState = new Map<string, { editsSinceNav: number }>();

// apply_patch carries its targets inside patchText instead of a filePath arg.
// Mirrors getEditTargetPaths() in api-verify-gate.mjs; kept local because this
// file is TypeScript compiled by OpenCode's own loader and importing across
// that boundary has bitten before.
const PATCH_TARGET_RE = /^\s*\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*(.+?)\s*$/gm;

function extractEditPath(args: unknown): string | undefined {
  if (!args || typeof args !== "object") return undefined;
  const a = args as Record<string, unknown>;
  const candidate = a.filePath ?? a.file_path ?? a.path;
  if (typeof candidate === "string") return candidate;

  const patchText = typeof a.patchText === "string" ? a.patchText
    : typeof a.patch === "string" ? a.patch
    : undefined;
  if (patchText === undefined) return undefined;

  // Report a .cs target when the patch has one — a mixed patch that touches
  // C# must still arm the gate, not be judged by whichever file came first.
  PATCH_TARGET_RE.lastIndex = 0;
  let first: string | undefined;
  let match: RegExpExecArray | null;
  while ((match = PATCH_TARGET_RE.exec(patchText)) !== null) {
    const p = match[1];
    if (!p) continue;
    if (first === undefined) first = p;
    if (p.toLowerCase().endsWith(".cs")) return p;
  }
  return first;
}

function toolOutputFailed(output: unknown): boolean {
  if (!output || typeof output !== "object") return true;
  const result = output as Record<string, unknown>;
  if (result.isError === true || result.error || result.errorMessage) return true;
  if (typeof result.output === "string" &&
      /^\s*(?:error|failed|edit blocked|tool execution failed)\b/i.test(result.output)) {
    return true;
  }
  return callFailed(result);
}

// ─── Plugin export ───────────────────────────────────────────────────────────
const flaxmcpNavPlugin: Plugin = async ({ project, directory, $ }) => {
  const root = project?.worktree || directory || process.cwd();
  const exe = findExe(root);
  const exeExists = fs.existsSync(exe);

  return {
    // Deliberately NO daemon shutdown here: the daemon is shared across every
    // concurrent agent session on this machine. See lifecycle note at top.
    dispose: () => { },

    config: async (cfg) => {
      // Pre-warm the daemon on session open so the first AI call is instant.
      // Deliberately NOT awaited: ensureDaemon()'s worst-case cold-start path
      // (spawnDaemon's two-phase retry — up to 30s poll, 6s dead-check, 30s
      // retry) was blocking opencode's session-open `config` hook for ~70s
      // (measured 2026-08-04). callAtomic() already self-heals via its own
      // ensureDaemon() call on first real use (line ~365), and shares the
      // same module-level daemonStartPromise, so a slow prewarm here loses
      // nothing but the "first call is instant" optimization — it never
      // needs to hold up the session itself.
      (async () => {
        try {
          const r = await ensureDaemon(root);
          if (r.ok) {
            // Warm the four indexes in parallel with a 5s overall timeout
            const warmup = Promise.allSettled([
              pipeCall({ atomic: "csharp/symbol_search", args: { query: "IFlaxBridge", maxResults: 1 } }),
              pipeCall({ atomic: "docs/grep", args: { query: "AGENTS", maxResults: 1 } }),
              pipeCall({ atomic: "docs/find_capability", args: { query: "bridge", maxResults: 1 } }),
              pipeCall({ atomic: "flax_api/lookup", args: { name: "Actor", maxResults: 1 } }),
            ]);
            await Promise.race([warmup, delay(5000)]);
          }
        } catch {
          // non-fatal; the tool will retry on first use
        }
      })();
    },

    "tool.execute.before": async (input, output) => {
      if (process.env.FLAXMCP_NAV_GATE_DISABLE === "1") return;

      let shouldBlock = false;
      let target = "";
      try {
        // Verification is recorded in tool.execute.after, after the call
        // succeeds. A failed lookup must not unlock C# edits.
        if (isVerificationCall(input.tool, output?.args)) return;
        if (!EDIT_TOOLS.has(input.tool)) return;
        target = extractEditPath(output?.args) ?? "";
        shouldBlock = target.toLowerCase().endsWith(".cs");
        if (shouldBlock) {
          const state = sessionNavState.get(input.sessionID);
          if (!state) {
            // Never called flaxnav this session — block.
          } else if (!hasEditCredit(state)) {
            // Too many .cs edits since last flaxnav call — block.
            shouldBlock = true;
          } else {
            // Within the edit window — allow. Successful accounting happens
            // in the after hook, so downstream gate failures do not count.
            shouldBlock = false;
          }
        }
      } catch (err) {
        // Never let a bug in the gate itself brick an unrelated tool call.
        console.error("[flaxnav-gate] internal error, failing open:", err);
        return;
      }

      if (shouldBlock) {
        const message =
          `flaxnav-gate: blocked ${input.tool} on '${target}' — ` +
          (sessionNavState.has(input.sessionID)
            ? `too many .cs edits (${MAX_EDITS_PER_NAV}) since your last flaxnav call. `
            : `no flaxnav call yet this session. `) +
          `Call the flaxnav tool (e.g. {atomic:"csharp/symbol_search", args:{query:"..."}} or ` +
          `{atomic:"flax_api/lookup", args:{name:"..."}}) to verify the type/symbol actually exists ` +
          `before touching C#. See .opencode/skills/flax-nav/SKILL.md ("Mandatory API-first law"). ` +
          `Each flaxnav call unblocks the next ${MAX_EDITS_PER_NAV} .cs edits. ` +
          `Escape hatch (operator only): FLAXMCP_NAV_GATE_DISABLE=1.`;
        if (!setNavGateProblem(output, message)) throw new Error(message);
      }
    },

    "tool.execute.after": async (input, output) => {
      if (process.env.FLAXMCP_NAV_GATE_DISABLE === "1") return;

      try {
        const args = output?.args ?? input?.args;
        if (isVerificationCall(input.tool, args)) {
          if (toolOutputFailed(output)) return;
          const state = sessionNavState.get(input.sessionID) ?? createNavState();
          resetNavState(state);
          sessionNavState.set(input.sessionID, state);
          return;
        }

        if (!EDIT_TOOLS.has(input.tool)) return;
        const target = extractEditPath(args) ?? "";
        if (!target.toLowerCase().endsWith(".cs")) return;

        const state = sessionNavState.get(input.sessionID);
        if (!state || toolOutputFailed(output)) return;
        recordSuccessfulEdit(state);
      } catch (err) {
        // Accounting must never block the next unrelated tool call.
        console.error("[flaxnav-gate] after-hook internal error:", err);
      }
    },

    tool: {
      flaxnav: tool({
        description:
          "Semantic code/doc/Flax-API navigation via flaxmcp-nav daemon (single-digit ms, named pipe). " +
          "Use for C# refs/impls/callers, Flax API lookup, doc discovery. " +
          "Atomics: csharp/find_definition{symbolName}, csharp/find_references{symbolName}, " +
          "csharp/symbol_search{query}, csharp/find_implementations{symbolName}, " +
          "csharp/get_call_hierarchy{symbolName}, csharp/describe_symbol{symbolName}, " +
          "flax_api/lookup{name}, flax_api/search{query}, flax_api/members_of{name}, " +
          "flax_api/enum_values{name}, flax_api/inheritance_chain{name}, " +
          "docs/find_section{query}, docs/find_doc{query}, docs/grep{query}, " +
          "docs/find_capability{query}, docs/find_composes{query}. " +
          "Common args: target (slnf/csproj path), maxResults. " +
          "count:0 = symbol does NOT exist (terminal, do not retry).",
        args: {
          atomic: tool.schema
            .string()
            .describe(
              "Daemon atomic name. C#: csharp/find_definition, csharp/find_references, " +
              "csharp/symbol_search, csharp/find_implementations, csharp/get_call_hierarchy, " +
              "csharp/describe_symbol. Flax API: flax_api/lookup, flax_api/search, " +
              "flax_api/members_of, flax_api/enum_values, flax_api/inheritance_chain. " +
              "Docs: docs/find_section, docs/find_doc, docs/grep, docs/find_capability, " +
              "docs/find_composes."
            ),
          args: tool.schema
            .record(tool.schema.string(), tool.schema.any())
            .optional()
            .describe("Per-atomic args object. See flax-nav skill for each atomic's args."),
        },
        async execute(args, context) {
          const atomic: string = typeof args.atomic === "string" ? args.atomic : "";
          const a = (args.args ?? {}) as Record<string, unknown>;

          if (!ATOMICS.includes(atomic)) {
            return JSON.stringify({
              ok: false,
              error: "unsupported_atomic",
              terminal: true,
              retryable: false,
              message: `'${atomic}' is not a daemon atomic. Known: ${ATOMICS.join(", ")}`,
            });
          }

          const callRoot = context.worktree || context.directory || project?.worktree || directory || process.cwd();
          const result = await callAtomic(callRoot, atomic, a);

          // 2026-07-28: session-mining data (1,144 sessions) found 110
          // instances across 58 sessions where an agent got count:0 from
          // flaxnav and retried anyway (grep, variant spellings) despite
          // AGENTS.md/the flax-nav skill already stating this is terminal.
          // Advisory prose alone wasn't enough — inject the terminal note
          // directly into the tool result so it's unavoidable at the point
          // the count:0 signal actually arrives, not just documented
          // elsewhere. Gate lives in lib/flaxmcp-tool-aliases.core.mjs
          // (injectCountZeroTerminalNote) so the drift test covers it — the
          // daemon's hardened count:0 fields (terminal, retry:false, reason,
          // segmentSearched, indexScope) survive the round-trip there.
          return injectCountZeroTerminalNote(result);
        },
      }),
    },
  };
};

export default flaxmcpNavPlugin;
