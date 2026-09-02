# FlaxNav — Free C# Navigator for Flax Engine

Fast, editor-agnostic C# + Flax API + docs navigation daemon (named pipe, ~50-300ms).

## Why free

MIT, free for all Flax devs, no telemetry, local only. Extracted from [flax-mcp](https://github.com/flax-game-studio/flax-mcp) factory by Flax Game Studio, MIT licensed for community.

## Features

| Family | Atomics | Description |
|---|---|---|
| `csharp/*` | 10 | `find_definition`, `find_references`, `symbol_search`, `find_implementations`, `get_call_hierarchy`, `describe_symbol`, `find_related_symbols`, `list_plugin_tools`, `grep_symbol_context`, `index_health` |
| `flax_api/*` | 5 | `lookup`, `search`, `members_of`, `enum_values`, `inheritance_chain` (from FlaxEngine.CSharp.xml) |
| `docs/*` | 3+2 | `find_section`, `find_doc`, `grep` + capability stubs `find_capability`, `find_composes` |
| `atlas/*` + `plugin/*` + `receipt/*` | 4 | `atlas/diff_tree`, `plugin/catalog`, `receipt/search`, `receipt/recent`, `receipt/by_id` |

Named-pipe daemon (`\\.\pipe\flaxmcp-nav`), 8 concurrent handlers, Windows only. No Roslyn semantic model — managed source-text scan, so no heavy SDK install needed.

## OpenCode plugin (saves compilation fails)

The daemon works best with the included OpenCode plugin — it blocks C# edits until you verify the real API via `flaxnav`, preventing `CS0117`/`CS0246` and stale-assembly confusion.

- **Per-file verification:** one `flaxnav` call (`flax_api/lookup`, `flax_api/members_of`, `csharp/symbol_search`, etc.) authorizes the next `.cs` file only; a different file needs its own check. 30-minute timeout.
- **3-edit window:** each `flaxnav` call unblocks the next 3 `.cs` edits (`MAX_EDITS_PER_NAV=3`), then re-blocks — stops "one lookup then guess forever".
- **Unity-ism blocking:** refuses Unity-only code (`MonoBehaviour` → `Script`, `GameObject` → `Actor`, `Rigidbody` → `RigidBody`, `Time.deltaTime` → `Time.DeltaTime`, `using UnityEngine` → `FlaxEngine`) and names the Flax fix inline.
- **Compile-breaker blocking:** catches guaranteed `CS0104`/`CS1002` before the write.

Install: copy `opencode-plugin/` into your project's `.opencode/plugin/` or add to `opencode.jsonc`:

```jsonc
"plugin": ["./opencode-plugin/flaxmcp-nav.ts", "./opencode-plugin/cs-edit-gates.mjs"]
```

See [`opencode-plugin/README.md`](opencode-plugin/README.md) for install, usage, and the per-file binding details.

## Requirements

- .NET 8 SDK
- Windows 10/11 (named pipe)

## Install

Option A — build from source:

```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# exe -> bin/Release/net8.0/flaxmcp-nav.exe
```

Option B — download Release zip (see Releases page): `flax-nav-v2.2.1-win-x64.zip` contains `flaxmcp-nav.exe`, `nav.ps1`, `README.md`, `LICENSE`.

## Quick start

```pwsh
# one-shot (no daemon) — builds indexes on demand
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5
.\bin\Release\net8.0\flaxmcp-nav.exe docs/find_section query=flax

# via wrapper (auto-spawn opt-in)
pwsh nav.ps1 csharp/symbol_search query=Bridge
FLAXMCP_NAV_AUTOSPAWN=1 pwsh nav.ps1 csharp/find_definition symbolName=Player
# explicit daemon
flaxmcp-nav.exe --daemon        # listen forever
flaxmcp-nav.exe --health        # JSON health report
flaxmcp-nav.exe --status
flaxmcp-nav.exe --warm          # warm all indexes in-process (no pipe)
```

Wrapper `nav.ps1` defaults to `FLAXMCP_NAV_AUTOSPAWN=1` in community build (auto-spawn if daemon missing). Set `FLAXMCP_NAV_AUTOSPAWN=0` for connect-only.

## Build & test

```pwsh
dotnet build flaxmcp-nav.csproj -c Release
.\bin\Release\net8.0\flaxmcp-nav.exe --health
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Program maxResults=3
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=2
```

Expected: `dotnet build` succeeds with 0 errors. `--health` returns `{ok:true, ...}`. `csharp/symbol_search` returns `count>0`.

## Logs

Daemon log: `%TEMP%\flaxmcp-nav\daemon.log` (rotated at 5 MB, 3 rotations). Shadow copies under `%TEMP%\flaxmcp-nav\shadow\`.

## Community vs factory build

- This repo is self-contained: vendored `SwallowedCatch.cs` (namespace `FlaxMcp.NavDaemon`, no `FlaxMcp.Core` reference).
- `docs/find_capability` and `docs/find_composes` are stubs returning `{ok:true, count:0, hits:[]}` — capability index not included in community build (requires private `CapabilityIndex` scanner). The factory build vendors the full scanner; community build prefers a green stub over a stale copy. To restore full capability search, vendor `src/FlaxMcp.Core/Core/Capabilities/CapabilityIndex.cs` into `FlaxMcp.NavDaemon` and wire `Program.DocAtomics.cs`.

## License

MIT — Copyright (c) 2026 Flax Game Studio. See LICENSE.

## Links

- Original factory: `flax-mcp` (private) — this daemon extracted as MIT for community.
- Flax Engine: https://flaxengine.com

