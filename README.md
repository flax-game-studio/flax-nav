# FlaxNav by Flax Game Studio

Fast C# + Flax API navigator for Flax Engine. MIT.

This is part of my own toolbox. I built it for daily Flax development and it has saved me a lot of time. It is useful on its own, so I am sharing it.

## What it does

**C# navigation (`csharp/*`) — 10 atomics:**
`find_definition`, `find_references`, `symbol_search`, `find_implementations`, `get_call_hierarchy`, `describe_symbol`, `find_related_symbols`, `grep_symbol_context`, `list_plugin_tools`, `index_health`
Search your whole `Source/` for types, methods, and usages without opening the editor.

**Flax API lookup (`flax_api/*`) — 5 atomics:**
`lookup`, `search`, `members_of`, `enum_values`, `inheritance_chain`
Reads `FlaxEngine.CSharp.xml` — answers what exists in Flax, what members a type has, and what enum values are valid.

**Docs (`docs/*`):**
`find_section`, `find_doc`, `grep` across markdown. Capability stubs `find_capability`/`find_composes` return empty in this build.

## How it works

A small daemon listens on `\\.\pipe\flaxmcp-nav` on Windows.

- **No Roslyn, no editor required.** Managed source-text scan, so no heavy SDK install and no Flax Editor needed to be open.
- **Indexes on demand.** First call scans `Source/**/*.cs` (about 4,000 files / 100k symbols in our project). Result is cached in memory and on disk. File watcher + TTL updates it incrementally.
- **8 concurrent handlers.** Queries run in parallel, typical response 50-300ms.
- **Wrapper `nav.ps1`.** `pwsh nav.ps1 csharp/symbol_search query=Bridge` works whether the daemon is running or not. With `FLAXMCP_NAV_AUTOSPAWN=1` (default in this build) it starts the daemon if missing. Set `0` for connect-only.
- **Health and warmup.** `flaxmcp-nav.exe --health` returns file/symbol counts and readiness. `flaxmcp-nav.exe --warm` builds all indexes in-process.

Shadow copies of edits go to `%TEMP%\flaxmcp-nav\shadow\` for debugging.

## How the OpenCode plugin saves time

This is the part that saved me the most. It prevents wasted edits that fail to compile.

The daemon alone is a search tool. The plugin (`opencode-plugin/`) makes your editor and AI verify the API *before* the write. Without it, a model writes `MonoBehaviour` or a wrong Flax member, the C# build fails, the editor scripting layer goes down, and the bridge dies — you lose time restarting.

### What it blocks and how

**1. Per-file API verification (30-minute window)**
You must call `flaxnav` before editing a `.cs` file. One verification authorizes the *next* `.cs` file only. Editing `Player.cs` does not authorize `Enemy.cs`.

What counts as verification:
- `flax_api/lookup` — does this Flax type exist?
- `flax_api/members_of` / `enum_values` — does this member/enum case exist?
- `csharp/find_definition`, `symbol_search`, `describe_symbol`, etc. — does this repo type exist?

After 30 minutes the proof expires and you verify again.

**2. 3-edit window**
Each `flaxnav` call unlocks the next 3 `.cs` edits (`MAX_EDITS_PER_NAV=3`). The 4th edit without a new `flaxnav` call is blocked. This stops the pattern of looking up one type and then guessing five more.

**3. Error message tells the fix**
When blocked, you see:
```
API-VERIFY-GATE: blocked edit on 'Foo.cs' — no verification for this file. Call flaxnav with flax_api/lookup or csharp/symbol_search first.
```
or
```
flaxnav-gate: blocked edit on 'Foo.cs' — 3 edits since last flaxnav, verify again.
```
The message names the exact atomic to call. The next edit after a correct `flaxnav` succeeds.

**4. Unity code detection (20 rules)**
Flax and Unity share names like `Vector3` and `Debug.Log`, so naive blocking would break correct code. The gate only blocks Unity-only symbols or casing differences that cannot compile against Flax:

`using UnityEngine` → `using FlaxEngine`, `MonoBehaviour` → `Script`, `GameObject` → `Actor`, `GetComponent<T>` → `GetScript<T>`, `Instantiate` → `PrefabManager.SpawnPrefab`, `Camera.main` → `Camera.MainCamera`, `transform.position` → `Actor.Position`, `Rigidbody` → `RigidBody`, `Time.deltaTime` → `Time.DeltaTime`, and 12 more.

Comment lines are skipped, so discussing Unity in comments is allowed.

**5. Compile-breaker detection**
Two patterns that are guaranteed to break the `Source/Game` build:

- `using FlaxEngine` + `using System.Numerics` + bare `Vector3` — ambiguous `CS0104` (both namespaces define `Vector3`). The factory's `Game.Build.cs` always references `System.Numerics.Vectors`, so this fails every time.
- `??=` with certain `LangVersion` settings — `CS1002` in this project setup.

The gate refuses the write and says to add an alias (`using Vector3 = FlaxEngine.Vector3;`) or avoid `??=`.

**6. When the reuse gate applies**
In this community build the reuse gate auto-passes when no inventory file exists on disk (`.agents/gameside-inventory.generated.md`). In my private factory it also enforces that you check existing code before creating a new type, but that check is disabled here so the plugin works in any project.

To disable the nav gate locally (operator only): `FLAXMCP_NAV_GATE_DISABLE=1`.

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
Download `flax-nav-v2.3.0-with-plugin-win-x64.zip` from GitHub Releases. It contains `flaxmcp-nav.exe`, `flaxmcp-nav.dll`, dependencies, `nav.ps1`, and `opencode-plugin/`.

## Quick start

```pwsh
# One-shot (no daemon needed — indexes build on demand)
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5
.\bin\Release\net8.0\flaxmcp-nav.exe docs/find_section query=flax

# Via wrapper
pwsh nav.ps1 csharp/symbol_search query=Bridge
FLAXMCP_NAV_AUTOSPAWN=1 pwsh nav.ps1 csharp/find_definition symbolName=Player

# Daemon control
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health
flaxmcp-nav.exe --status
flaxmcp-nav.exe --warm
```

## OpenCode plugin install

Copy the folder into your project:

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

Add to `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

Or:
```jsonc
"plugin": ["./opencode-plugin/flaxmcp-nav.ts", "./opencode-plugin/cs-edit-gates.mjs"]
```

`flaxmcp-nav.ts` provides the `flaxnav` tool and the 3-edit gate. `cs-edit-gates.mjs` adds the per-file verification, Unity, and compile-breaker checks.

Details: see `opencode-plugin/README.md`.

## Logs

Daemon log: `%TEMP%\flaxmcp-nav\daemon.log` (rotated at 5 MB, 3 rotations). Health checks log there as well.

## License

MIT — Copyright (c) 2026 Flax Game Studio. See `LICENSE`. This build is self-contained (vendored `SwallowedCatch.cs`, no `FlaxMcp.Core` reference).
