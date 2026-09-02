# FlaxNav by Flax Game Studio

Fast way to find C# code and Flax API. Free, MIT.

This is from my own toolbox. I made it for my daily Flax work. It saved me a lot of time. I am sharing it. It works on its own.

## Why I made it

Flax code looks like Unity code but is not the same. In Unity you write `MonoBehaviour`, in Flax it is `Script`. In Unity `Rigidbody`, in Flax `RigidBody`. AI knows Unity better so it writes wrong Flax code. You try to build, it fails, the editor closes the scripting part, you wait 5 minutes. Again and again.

This tool answers fast without opening the editor: "does this name exist in Flax?" and "where is this code in my project?" The plugin then stops you from saving wrong code.

## What it does

- Find C# code in your project: find where a type is made, where it is used, search for a name, show call list.
- Check Flax API: does this Flax type exist? What members does it have? What values can this enum have?
- Search docs: find a section or text in markdown files.

## How it works

It is a small program that runs in the background on Windows. It listens on a named pipe `\\.\pipe\flaxmcp-nav`.

- No big tools needed. It does not need Roslyn or the Flax Editor open.
- On first use it reads all your `Source/**/*.cs` files. About 4,000 files in our project. It makes a list: which word is in which file and on which line. It is saved in memory. Next search is 0.05 to 0.3 seconds.
- It watches files. If you change a `.cs` file it only reads that one file again. That is fast, less than 1ms. If too many files change at once (like a build), it scans the folder and only reloads changed files.
- It can do 8 things at the same time.
- Flax API is read from `FlaxEngine.CSharp.xml`. About 16,000 names. Built once, about 20ms.
- Docs are read from all `*.md` files, about 1,200 files. It saves all headers and text lowercased for fast search.

You can run it one time like `flaxmcp-nav.exe csharp/symbol_search query=Bridge` or keep it running as a daemon `flaxmcp-nav.exe --daemon`. The helper `nav.ps1` starts it by itself if it is not running.

Other commands: `--health` shows if it is ready and how many files it knows, `--status` shows if daemon is running, `--warm` loads all lists early so first search is not slow, `--shutdown` stops it.

Files: log is at `%TEMP%\flaxmcp-nav\daemon.log` (new file after 5 MB), copies of your edits are at `%TEMP%\flaxmcp-nav\shadow\`.

## How the OpenCode plugin saves time

The plugin is in `opencode-plugin/`. It stops you and the AI from saving wrong `.cs` code. Without it you save, build fails, editor dies. With it the save is blocked *before* you build and it tells you what to fix.

### Two parts

**1. flaxmcp-nav.ts — the tool and 3-save rule**
It adds a tool called `flaxnav` to OpenCode. You call it before you edit.

It remembers per session how many `.cs` saves you did since last check. You get 3 saves per check. On the 4th save without a new check it blocks and says: "too many saves since last check, call flaxnav again."

What counts as a check: any `flaxnav` call that looks up real code and gets a good answer, like `flax_api/lookup` (does this Flax type exist?) or `csharp/symbol_search` (does this type exist in my project?). If the answer is "found 0", it does not count.

**2. cs-edit-gates — check per file + Unity mistakes + broken code**

It blocks `edit`, `write`, `apply_patch` on `.cs` files.

- **Per file check (30 minutes):** One check for `Player.cs` does not allow `Enemy.cs`. Each file needs its own check. After 30 minutes you need to check again.

- **Unity mistakes — 20 rules:** Flax and Unity share some names like `Vector3` so it does not block those. It only blocks names that cannot work in Flax:

  `using UnityEngine` -> use `FlaxEngine`
  `using UnityEditor` -> Flax editor code is in a plugin, not in Source/Game
  `UnityEngine.` -> `FlaxEngine.`
  `MonoBehaviour` -> `Script`
  `GameObject` -> `Actor`
  `GetComponent` -> `GetScript` or `GetChild`
  `[SerializeField]` -> `[Serialize]`
  `Instantiate` -> `PrefabManager.SpawnPrefab`
  `Camera.main` -> `Camera.MainCamera`
  `transform.position` -> `Actor.Position`
  `Rigidbody` -> `RigidBody`
  `Time.deltaTime` -> `Time.DeltaTime`
  plus 8 more small traps.

  Comments are not checked, so talking about Unity in a comment is fine.

- **Broken code — 2 rules that always fail to build in this project:**

  1. You write `using FlaxEngine;` and `using System.Numerics;` and then bare `Vector3`. Both have `Vector3` so the build does not know which one you mean. Fix: `using Vector3 = FlaxEngine.Vector3;`
  2. You write `??=` — fails on this project's build settings.

  These two are always blocked, even if you checked the API before. A good check does not make `MonoBehaviour` work.

- **When it is blocked you see the fix:** 
  `blocked edit on 'Source/Game/Foo.cs' — no check for this file. Call flaxnav with flax_api/lookup first.`
  or `found 'MonoBehaviour' — use 'Script' in Flax.`
  You fix in one edit.

In this free build the "check if type already exists somewhere else" rule is off when you have no inventory file. So you only get the useful checks.

### How they work together — real example

```
1. flaxnav lookup Actor              -> found, OK for Actor.cs
2. edit Actor.cs                     -> allowed (1 of 3)
3. edit Other.cs                     -> blocked (Other.cs needs its own check)
4. flaxnav search Other              -> found, OK for Other.cs
5. edit Other.cs                     -> allowed
```

Use both plugins together. One gives the tool and 3-save rule, the other checks the file content.

## What you need

- .NET 8
- Windows 10 or 11

## Install

From source:
```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# -> bin/Release/net8.0/flaxmcp-nav.exe
```

From Release:
Download `flax-nav-v2.3.0-with-plugin-win-x64.zip` from GitHub Releases. It has the exe, dlls, `nav.ps1`, and `opencode-plugin/`.

## Quick start

```pwsh
# One time (no daemon needed)
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5

# With helper
pwsh nav.ps1 csharp/symbol_search query=Bridge

# Keep daemon running
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health
flaxmcp-nav.exe --warm
```

## OpenCode plugin install

Copy the folder to your project:

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

Add to `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

See `opencode-plugin/README.md` for more.

To turn off the block for testing: `FLAXMCP_NAV_GATE_DISABLE=1`.

## Logs

- Daemon log: `%TEMP%\flaxmcp-nav\daemon.log`
- Copies: `%TEMP%\flaxmcp-nav\shadow\`
- `flaxmcp-nav.exe --health` shows files, symbols, members, watcher on/off.

If searches are slow after a build, the watcher had too many changes. Next search reloads only changed files. Check the log for `InternalBufferOverflow`.
If Flax API says "xml not found", set `FLAXMCP_FLAX_XML` to your `FlaxEngine.CSharp.xml` path.

## License

MIT — Flax Game Studio. See `LICENSE`.
