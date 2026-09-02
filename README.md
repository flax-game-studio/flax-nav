# FlaxNav — by Flax Game Studio

Fast C# + Flax API navigator for Flax Engine. Standalone, MIT.

> Part of the Flax Game Studio ecosystem. Works on its own. More exists.

## What it does

| Family | Atomics |
|---|---|
| `csharp/*` | `find_definition`, `find_references`, `symbol_search`, `find_implementations`, `get_call_hierarchy`, `describe_symbol`, `find_related_symbols`, `index_health` |
| `flax_api/*` | `lookup`, `search`, `members_of`, `enum_values`, `inheritance_chain` (from `FlaxEngine.CSharp.xml`) |
| `docs/*` | `find_section`, `find_doc`, `grep` |

Plus `atlas/diff_tree`, `plugin/catalog`, `receipt/*` for factory users.

## How it works

Daemon on `\\.\pipe\flaxmcp-nav` — 8 concurrent handlers, Windows. Managed source-text scan (no Roslyn, no heavy SDK). Indexes build on demand or via `--warm`, ~50-300ms per query. File watcher + TTL keeps index fresh.

## Requirements

- .NET 8 SDK, Windows 10/11

## Install

**From source:**
```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# -> bin/Release/net8.0/flaxmcp-nav.exe
```

**From Release:**
Download `flax-nav-v2.3.0-with-plugin-win-x64.zip` from Releases — contains `flaxmcp-nav.exe`, deps, `nav.ps1`, `opencode-plugin/`.

## Quick start

```pwsh
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5

pwsh nav.ps1 csharp/symbol_search query=Bridge
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health
```

`nav.ps1` defaults to `FLAXMCP_NAV_AUTOSPAWN=1` (auto-spawn if daemon missing). Set `0` for connect-only.

## OpenCode plugin

Blocks `edit`/`write` on `.cs` until you verify the real API via `flaxnav`. Prevents `CS0117`/`CS0246` and Unity slips.

- Per-file verification (30 min), 3-edit window (`MAX_EDITS_PER_NAV=3`)
- Unity-ism detection (`MonoBehaviour`→`Script`, `GameObject`→`Actor`, `Rigidbody`→`RigidBody`, `Time.deltaTime`→`Time.DeltaTime`)
- Compile-breaker detection (`CS0104` vector ambiguity, `??=`)

```jsonc
"plugin": ["./opencode-plugin/flaxmcp-nav.ts", "./opencode-plugin/cs-edit-gates.mjs"]
```

See `opencode-plugin/README.md` for details.

## Logs

`%TEMP%\flaxmcp-nav\daemon.log` (rotated at 5 MB). Shadow copies in `%TEMP%\flaxmcp-nav\shadow\`.

## License

MIT — Copyright (c) 2026 Flax Game Studio. See `LICENSE`.
