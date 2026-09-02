# FlaxNav by Flax Game Studio

Fast C# + Flax API navigator for Flax Engine. MIT. Works on its own.

This is part of my own toolbox. I built it for daily Flax work and it has saved me a lot of time — fewer wrong API guesses, fewer compile breaks, fewer editor restarts. I am sharing it as a standalone tool.

## Why it exists

Flax Engine C# is close to Unity C# but not the same. `MonoBehaviour` does not exist here, `Rigidbody` is `RigidBody`, `Time.deltaTime` is `Time.DeltaTime`. Flax Editor search is editor-bound and slow, offline API docs are XML, and AI models trained on Unity hallucinate members that do not exist. A wrong edit fails the build, the scripting layer goes down, the bridge dies, and you lose 5 minutes restarting.

FlaxNav fixes that by answering two questions in 50-300ms without the editor: "does this type/member exist in Flax?" and "where is this symbol in my repo?" The OpenCode plugins then enforce that answer before any `.cs` write.

## Architecture at a glance

```
editor / AI --(JSON line)--> \\.\pipe\flaxmcp-nav --(dispatch)--> daemon
                                                    |
                         +--------------------------+--------------------------+
                         |                          |                          |
                    CsSourceIndex            FlaxApiIndex                DocsIndex
                 (Source/**/*.cs)     (FlaxEngine.CSharp.xml)         (repo/**/*.md)
                 file watcher TTL        + FlaxEngine.CSharp.dll       header + grep
                                                    |                          |
                         +--------------------------+--------------------------+
                                                    |
                                              --(JSON)--> client
```

One daemon process, 8 concurrent handlers, Windows named pipe. The rest is stateless JSON over a line.

## How the daemon works

### Binary and startup

`flaxmcp-nav.exe` (net8.0, `FlaxMcp.NavDaemon`, ~170KB) + `Microsoft.Data.Sqlite` + `Newtonsoft.Json`. No Roslyn, no Flax SDK install.

Entry `Program.Main` parses `--daemon` / `--health` / `--status` / `--warm` / `--shutdown` / `--auto-start` / `atomic key=value` one-shots. Default `nav.ps1` uses `--auto-start` with `FLAXMCP_NAV_AUTOSPAWN=1` — if the pipe is missing it spawns the daemon and retries.

On daemon start: `RunDaemonEntry` opens the pipe (`CreateNamedPipe`, `PipeSecurity` allow current user), starts `PipeMonitor` (watches for stale pipe) and `AcceptPump` (loops `WaitForConnection`). Each connection is handed to a handler on the thread pool.

### Concurrency and lifecycle

- `SemaphoreSlim _gate(8,8)` — at most 8 handlers run at once. `_activeHandlers` / `_peakHandlers` tracked. Excess connections queue.
- `CancellationToken` per request via `AsyncLocal` — if the pipe disconnects, handlers bail early.
- Control verbs: `__ping__`, `__status__`, `__health__`, `__help__`, `__warmup__`, `__shutdown__` (needs `force=true`, refused while campaign-locked), `__campaign_lock__`/`__campaign_unlock__` (keep daemon alive across a multi-step workflow, in-memory only).
- `--warm` eagerly builds docs + Flax API + C# indexes in background so the first real query does not pay ~5s cold cost.

### C# index — the expensive part

`Program.CsharpManaged.cs` implements `CsSourceIndex` — immutable, atomically swapped via `Volatile.Read`/`Write`, read lock-free after warm.

**What is indexed:**
- Every `Source/**/*.cs` under the repo root, skipping `.git`, `.vs`, `bin`, `obj`, `node_modules`.
- Per file: `string[] Lines`, `IReadOnlyDictionary<string, int[]> Tokens` (identifier token → distinct 1-based lines it occurs on), `Length`, `LastWriteUtc`.

**How it builds:**
1. `EnumerateCsFiles` — parallel BFS, level-synchronous. Uses `EnumerateFiles`/`EnumerateDirectories` (streaming, not `GetFiles` arrays) and reuses `DirectoryInfo.Attributes` to skip reparse points. Found ~4,000 files / 870k lines in our repo.
2. `LoadSourceFile` — `File.ReadAllLines`, tokenize by `IsIdentifierStart` (`letter|_`) + `IsIdentifierPart` (`letter|digit|_`), dedupe per-line occurrences (so `_repoRootCached` on one line counts as 1, not 2).
3. `BuildLookups` — inverted index `token -> string[] owners` (files containing it). Presized to 128k buckets, sorted by path at birth so lookups stay sorted for free. Empty rehash avoided.

Timings logged per phase: walk ~200ms, load ~parallel, invert ~ms. Full build ~19s cold; incremental is ms.

**How it stays fresh:**
- `FileSystemWatcher` on `*.cs` with `IncludeSubdirectories=true`, `InternalBufferSize=65536` (default 8KB overflows on any build that touches `bin/obj`). Filter is applied *after* Windows queues events for the whole subtree, so `bin/obj` churn would overflow the buffer — this is why the watcher skips those dirs on the indexing side, not the OS side.
- `InvalidateSourceIndex` does not drop the whole index. It records `ConcurrentDictionary<string,byte> _dirtyPaths`. Next query calls `ApplyDirtyPaths` — reload only dirty indexed paths (~0.5ms each) via `CsSourceIndex.With(upserts, removals)` which shares untouched token sets.
- If the watcher overflows (`SourceWatcherError`, 495 occurrences observed in one log), `RequiresFullRebuild` is set and next query does `ResyncSourceIndex` — directory scan + diff by `Length/LastWriteUtc`, reload only changed files, not a blind rebuild.
- TTL fallback: `SourceIndexTtl = 5000ms` when watcher down, `1800000ms` (30 min) when watcher live — safety net only.

**How queries use it:**
- `ManagedFindAllReferences` — `LocationsOf(token)` union, skip `IsCommentOrString` lines.
- `ManagedGrepRegexIndexed` — index narrows to candidate lines, then `Regex.IsMatch` + `LineContainsExactCodeToken` (code-only, skips attributes, `using`, comments, strings).
- `ManagedSubstringTokenSearch` for `symbol_search` (`\w*query\w*`) — previously unindexed and took 60-255s; now `TokensContaining(fragment)` union → a few hundred lines, same hits, same order.
- `Regex` cache with `IgnoreCase | CultureInvariant` and 1s timeout.

### Flax API index

`FlaxApiIndex` reads `FlaxEngine.CSharp.xml` (from `FLAXMCP_FLAX_XML` env or `C:\Program Files (x86)\Flax\Flax_1.12\Binaries\Editor\Win64\{Development,Debug,Release}\FlaxEngine.CSharp.xml`).

- Parses `<member name="T:FlaxEngine.Actor">` etc. Kind letter: `T` type, `M` method, `P` property, `F` field, `E` event. Strips `(params)` overload suffix.
- `SimpleName` after last `.`, `DeclType` owner, `Namespace`, `BaseType` from `<Base><TypeName>` if present, `Summary`/`Remarks`/`Returns`/`Parameters` via `Flatten` (collapses whitespace, resolves `<see cref>`, `<paramref>`).
- `LowerHaystack = simple + full + summary + ns + declType` lowercased for substring search.
- After XML, reads `FlaxEngine.CSharp.dll` via `System.Reflection.Metadata` (never `Assembly.Load` — avoids `Newtonsoft.Json` fork bind failure). Populates `BaseType` for every type and `ObsoleteAttribute` messages keyed as `T:FlaxEngine.Actor` / `M:FlaxEngine.Actor.Position`.

Built once, ~16k members, ~20ms. `EnsureBuilt` double-checked lock.

### Docs index

`DocsIndex` scans `repo/**/*.md` once (~1,217 files / 12 MB, <5s), caches `DocHeader` (path, rel, line, depth, text) and `DocFile` (LowerBody). Skips `bin`, `obj`, `external`, `node_modules`, `.git`, `.opencode/cache|index`, `.cache`. `FileSystemWatcher` debounced 800ms, rebuilds on change, 60s stale fallback if watcher down.

### Dispatch, cache, shadow

- `Program.Dispatch` maps 21 atomics (`csharp/*`, `flax_api/*`, `docs/*`, `atlas/diff_tree`, `plugin/catalog`, `receipt/*`) + 8 control verbs to handlers.
- `ConcurrentDictionary<string,(JObject,LinkedListNode)> _cacheStore` + `_lruOrder` LRU, 512 entries, `lock _lruLock`. Hits/misses counted.
- `Program.Shadow` copies edited file snapshots to `%TEMP%\flaxmcp-nav\shadow\` before write (for post-mortem when a compile breaks).
- `DaemonLog` writes `%TEMP%\flaxmcp-nav\daemon.log` rotated at 5 MB ×3.

### Protocol

Client sends one JSON line per request: `{"atomic":"csharp/symbol_search","query":"Bridge","maxResults":10}` plus optional `repoRoot`. Daemon responds with one JSON line: `{"ok":true,"count":2,"hits":[...]}` or `{"ok":false,"error":"...","errorCode":"..."}`. Every result is wrapped `{data:{...}, isError:false}` by the bridge — gates unwrap via `unwrapEnvelope`.

`MaxRequestBytes = 1 MB`. Control verbs return JSON help/status/health.

## How the OpenCode plugins save time

Two plugins ship in `opencode-plugin/`. They are small, but together they prevent the loop: AI writes Unity code → build fails → editor dies → manual fix → repeat.

### `flaxmcp-nav.ts` — the tool + 3-edit gate

Registers MCP tool `flaxnav` (inputs: `atomic` + `args`). `atomic` enumerates all daemon atomics; `args` is free-form passthrough to the daemon. The plugin spawns the daemon if missing (same named pipe).

Gate state per OpenCode session (`Map<sessionID, {editsSinceNav:number}>`):

- `isVerificationCall(tool,args)` returns true for any `flaxnav` call whose atomic is `flax_api/*` or `csharp/*` and whose result is `ok:true` and not `count:0` (and not `unsupported_atomic`). That call is proof you checked reality.
- `hasEditCredit` allows `MAX_EDITS_PER_NAV=3` `.cs` edits after a verification. `recordSuccessfulEdit` increments. `resetNavState` on verification success. After 3, `hasEditCredit` is false and `tool.execute.before` on `edit`/`write`/`apply_patch` for `*.cs` throws with `setNavGateProblem` — the composition layer in `command-guards.mjs` turns it into a thrown error with the fix hint.
- The gate message is precise: `flaxnav-gate: blocked edit on 'Foo.cs' — too many .cs edits (3) since your last flaxnav call. Call flaxnav with flax_api/lookup or csharp/symbol_search first.`

This alone stops the "one lookup then guess forever" pattern.

### `cs-edit-gates.core.mjs` — per-file verification, Unity, compile-breaker

Thin wrapper `cs-edit-gates.mjs` re-exports `lib/cs-edit-gates.core.mjs` (706 lines, patched for community). Composed in my factory via `command-guards.mjs`; in community use it directly.

FILE_EDIT_TOOLS = `edit`, `write`, `apply_patch` (plus alias shapes).

**Per-file binding:**
`FILE_EDIT_TOOLS` edits carry `filePath`. The gate keeps `Map<filePath, lastVerificationTime>`. A verification for `Player.cs` does not authorize `Enemy.cs`. Each file needs its own `flaxnav` within 30 minutes. This is stricter than the 3-edit window.

**Unity detection — 20 rules from `lib/unity-isms.core.mjs` (223 lines, restored from `475568d1e`):**

| Pattern | Unity | Flax |
|---|---|---|
| `using UnityEngine` | `using UnityEngine` | `using FlaxEngine` |
| `using UnityEditor` | `using UnityEditor` | `FlaxEditor` lives in plugin, not `Source/Game` |
| `UnityEngine.` | `UnityEngine.*` | `FlaxEngine.*` |
| `MonoBehaviour` | `MonoBehaviour` | `Script` |
| `GameObject` | `GameObject` | `Actor` |
| `GetComponent<T>` | `GetComponent<T>` | `GetScript<T>` / `GetChild<T>` |
| `[SerializeField]` | `[SerializeField]` | `[Serialize]` |
| `Instantiate` | `Instantiate` | `PrefabManager.SpawnPrefab` |
| `Camera.main` | `Camera.main` | `Camera.MainCamera` |
| `transform.position` | `transform.position` | `Actor.Position` |
| `Rigidbody` | `Rigidbody` | `RigidBody` (capital B) |
| `Time.deltaTime` | `Time.deltaTime` | `Time.DeltaTime` |
| + 8 more casing traps | — | — |

Shared names (`Vector3`, `Quaternion`, `Color`, `Mathf`, `Debug.Log`, `Input.GetAxis`, `OnTriggerEnter`, `Time`, `Camera`, `Script`) are never flagged. Comment lines are skipped.

**Compile-breaker detection — 2 rules:**

1. `using FlaxEngine; using System.Numerics;` + bare `Vector3` / `Quaternion` — ambiguous `CS0104`. `Game.Build.cs` always references `System.Numerics.Vectors`, so this fails every build. Fix: `using Vector3 = FlaxEngine.Vector3;`.
2. `??=` — `CS1002` under the project's `LangVersion` despite `csproj` advertising 14.

Both are never waived by verification — a prior lookup does not make `MonoBehaviour` compile.

**Reuse gate (community-patched):**
In my factory it also blocks creating a new type that already exists elsewhere (checks `gameside-inventory.generated.md`). In this build: `if (!fs.existsSync(".agents/gameside-inventory.generated.md")) return true` — auto-passes, so any project without that inventory gets API+Unity+compile-breaker gates only.

**Error shape:**
```
API-VERIFY-GATE: blocked edit on 'Source/Game/Foo.cs' — no verification for this file. Call flaxnav with flax_api/lookup (for types) or flax_api/members_of (for members) or csharp/symbol_search first.
REUSE-VERIFY-GATE: ... (not in community unless inventory present)
UNITY-CODE-GATE: found `MonoBehaviour` — use `Script` in Flax.
COMPILE-BREAK-GATE: ambiguous Vector3 — add alias using Vector3 = FlaxEngine.Vector3;
```
Each names the Flax replacement inline so the retry is one edit.

### How they work together

A real session:

```
1. flaxnav flax_api/lookup name=Actor           -> ok:true (proof for Actor.cs, edit credit 3->0)
2. edit  Source/Game/Actor.cs                   -> allowed (credit 1/3, file proof consumed)
3. edit  Source/Game/Actor.cs                   -> allowed (2/3, same file needs re-verify? cs-edit-gates says per-file, so this would block — flaxmcp-nav allows, cs-edit-gates blocks. Together they are stricter.)
4. edit  Source/Game/Other.cs                   -> blocked by cs-edit-gates per-file rule (even though flaxmcp-nav credit remains)
5. flaxnav csharp/symbol_search query=Other     -> ok:true (proof for Other.cs)
6. edit  Source/Game/Other.cs                   -> allowed
```

Install both: `flaxmcp-nav.ts` covers the tool + window, `cs-edit-gates.mjs` covers content checks. Either alone helps; both together is what saved me time.

## Requirements

- .NET 8 SDK
- Windows 10/11 (named pipe)

## Install

**From source:**
```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# -> bin/Release/net8.0/flaxmcp-nav.exe
```

**From Release:**
Download `flax-nav-v2.3.0-with-plugin-win-x64.zip` from GitHub Releases. Contains `flaxmcp-nav.exe`, `flaxmcp-nav.dll`, deps (`Microsoft.Data.Sqlite`, `Newtonsoft.Json`, `SQLitePCLRaw.*`), `nav.ps1`, `opencode-plugin/`.

## Quick start

```pwsh
# One-shot (indexes build on demand)
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5
.\bin\Release\net8.0\flaxmcp-nav.exe docs/find_section query=flax

# Via wrapper (auto-spawn if daemon missing)
pwsh nav.ps1 csharp/symbol_search query=Bridge
FLAXMCP_NAV_AUTOSPAWN=1 pwsh nav.ps1 csharp/find_definition symbolName=Player

# Daemon control
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health
flaxmcp-nav.exe --status
flaxmcp-nav.exe --warm
flaxmcp-nav.exe --shutdown --force
```

`nav.ps1` defaults to `FLAXMCP_NAV_AUTOSPAWN=1`. Set `0` for connect-only.

## OpenCode plugin install

Copy the folder into your project:

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

Add to `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

`flaxmcp-nav.ts` = tool + 3-edit gate. `cs-edit-gates.mjs` = per-file + Unity + compile-breaker. See `opencode-plugin/README.md` for the `MAX_EDITS_PER_NAV`, `isVerificationCall`, and `gate-shared` helpers.

To disable locally (operator only): `FLAXMCP_NAV_GATE_DISABLE=1`.

## Logs and troubleshooting

- Daemon log: `%TEMP%\flaxmcp-nav\daemon.log` rotated at 5 MB ×3.
- Shadow copies: `%TEMP%\flaxmcp-nav\shadow\`.
- `flaxmcp-nav.exe --health` JSON: `ok`, `ready`, `version`, `uptime`, `csharpBackend`, `fileCount`, `symbolCount`, `memberCount`, `docsFileCount`, `watcherActive`, `cacheHits`/`Misses`.
- `flaxmcp-nav.exe --status` for daemon state.
- If source queries are slow after a build, the watcher overflowed — next query resyncs incrementally; check daemon.log for `InternalBufferOverflow`.
- If Flax API says `xml not found`, set `FLAXMCP_FLAX_XML` to your `FlaxEngine.CSharp.xml` full path.

## License

MIT — Copyright (c) 2026 Flax Game Studio. See `LICENSE`.
