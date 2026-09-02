# FlaxNav by Flax Game Studio

Fast C# + Flax API navigator for Flax Engine. MIT.

Part of Flax Game Studio ecosystem.

## What it does

- `csharp/*` — find_definition, find_references, symbol_search, get_call_hierarchy
- `flax_api/*` — lookup, search, members_of, enum_values (from FlaxEngine.CSharp.xml)
- `docs/*` — find_section, find_doc, grep

## How it works

Daemon on `\\.\pipe\flaxmcp-nav`. Managed scan, no Roslyn. ~50-300ms. File watcher keeps index fresh.

## Requirements

.NET 8, Windows 10/11

## Install

```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# or download zip from Releases
```

## Quick start

```pwsh
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics
pwsh nav.ps1 csharp/find_definition symbolName=Player
flaxmcp-nav.exe --health
```

## OpenCode plugin

Blocks `edit` on `.cs` until `flaxnav` verifies the API. Prevents CS0117/CS0246 and Unity code.

```jsonc
"plugin": ["./opencode-plugin/flaxmcp-nav.ts", "./opencode-plugin/cs-edit-gates.mjs"]
```

See `opencode-plugin/README.md`.

## License

MIT — Flax Game Studio
