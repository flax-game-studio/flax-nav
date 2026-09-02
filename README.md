# FlaxNav by Flax Game Studio

Fast C# and Flax API search for Flax Engine. Free and open source (MIT).

I built this for my own daily Flax work. It saved me hours of wrong API guesses, failed builds, and editor restarts, so I'm sharing it. It works on its own as a standalone tool.

## Why

Flax C# looks like Unity C# but the names are different. Unity's `MonoBehaviour` is `Script` in Flax. `GameObject` is `Actor`. `Rigidbody` is `RigidBody`. `Time.deltaTime` is `Time.DeltaTime`. AI models know Unity better than Flax, so they write the wrong names. You save, you build, it fails, the scripting part of the editor goes down, and you wait.

Flax Editor's own search also needs the editor open and is slow for large projects. And Flax API docs are an XML file, not easy to search.

FlaxNav answers two questions in under a second, without the editor: "does this Flax type or member actually exist?" and "where is this name in my code?"

## What it does

**Find your C# code (`csharp/*`)**
Search your `Source` folder without opening anything.
- `find_definition` — where a type or method is defined
- `find_references` — where it is used
- `symbol_search` — find names that contain a word
- `get_call_hierarchy`, `find_implementations`, `describe_symbol`, `find_related_symbols`
- `index_health` — how many files and symbols are indexed

**Check the Flax API (`flax_api/*`)**
Reads `FlaxEngine.CSharp.xml` (about 16,000 members).
- `lookup` — does this type exist?
- `search` — search by word across names and docs
- `members_of` — what members does this type have? Use this for members, not just `lookup`
- `enum_values` — what values can this enum have?
- `inheritance_chain` — what does this type inherit from?

**Search docs (`docs/*`)**
- `find_section`, `find_doc`, `grep` through your markdown files

Plus `atlas/diff_tree`, `plugin/catalog`, and `receipt/*` for factory workflows.

## How the daemon works

It's a small Windows program that runs in the background. It listens on `\\.\pipe\flaxmcp-nav`. You talk to it with JSON, it answers with JSON. No Flax install or Roslyn needed.

### Finding your code

On the first call it reads every `Source/**/*.cs` file. In our project that's about 4,000 files. It splits each file into words and remembers which word is on which line and in which file. After that a search is fast — usually 50 to 300 milliseconds.

It skips `bin`, `obj`, `.git`, `.vs`, and `node_modules`.

### Keeping the index fresh

It watches your `Source` folder. When you change a `.cs` file it only re-reads that one file — almost instant. If you build and hundreds of files change at once, it scans the folder and reloads only the files that actually changed. If the watcher misses something (Windows buffer overflow), the next search does a quick scan and fixes it. There is also a 30-minute safety rebuild if the watcher ever goes quiet.

### Checking the Flax API

It finds `FlaxEngine.CSharp.xml` from your Flax install (or from `FLAXMCP_FLAX_XML` if you set it). It reads every `<member>` tag — type, method, property, field, event — and saves the name, docs, and base type. For base types and deprecation notes it also reads `FlaxEngine.CSharp.dll` directly from the file, without loading it.

This index is built once, about 20 milliseconds, then reused.

### Talking to the daemon

You can call it one time:
```pwsh
flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
```
Or keep it running:
```pwsh
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health   # is it ready? how many files does it know?
flaxmcp-nav.exe --status
flaxmcp-nav.exe --warm     # load everything now so first search isn't slow
flaxmcp-nav.exe --shutdown --force
```

The helper `nav.ps1` does this for you. `pwsh nav.ps1 csharp/symbol_search query=Bridge` will start the daemon by itself if it's not running. Set `FLAXMCP_NAV_AUTOSPAWN=0` if you want it to fail instead of starting.

It handles 8 requests at once. Extra requests wait in line. If the connection drops, the work stops.

Logs are at `%TEMP%\flaxmcp-nav\daemon.log` (rotates at 5 MB). Copies of your edits are kept at `%TEMP%\flaxmcp-nav\shadow\` for debugging.

## The OpenCode plugin that saves time

The plugin is in `opencode-plugin/`. This is the part that saved me the most time. The daemon finds things. The plugin makes sure you actually checked before you save.

Without it: AI writes `MonoBehaviour`, you save, the build fails, the editor's scripting breaks, you fix it by hand, you lose minutes.

With it: the save is blocked *before* the build and it tells you exactly what to check.

### How the block works

**One check per file, lasts 30 minutes.**
A check for `Player.cs` does not count for `Enemy.cs`. Each file needs its own check. After 30 minutes you check again.

What counts as a check? Any `flaxnav` call that searched real code and found something, for example:
- `flax_api/lookup` — does this Flax type exist?
- `flax_api/members_of` or `enum_values` — does this member or enum value exist?
- `csharp/find_definition` or `symbol_search` — does this type exist in my project?

If the answer is "found 0", it doesn't count.

**Three saves per check.**
Each good check gives you 3 saves of `.cs` files. On the 4th save without a new check it blocks and says which `flaxnav` call to make. This stops "check once, guess five times."

**What it blocks:**

1. **Wrong file or no check:**
   `blocked edit on 'Source/Game/Foo.cs' — no check for this file. Call flaxnav with flax_api/lookup first.`

2. **Unity code that can't work in Flax (20 rules):**
   It only blocks names that definitely can't compile in Flax. Common names like `Vector3` are not blocked.

   `using UnityEngine` → use `FlaxEngine`
   `MonoBehaviour` → `Script`
   `GameObject` → `Actor`
   `GetComponent` → `GetScript` or `GetChild`
   `[SerializeField]` → `[Serialize]`
   `Instantiate` → `PrefabManager.SpawnPrefab`
   `Camera.main` → `Camera.MainCamera`
   `transform.position` → `Actor.Position`
   `Rigidbody` → `RigidBody`
   `Time.deltaTime` → `Time.DeltaTime`
   plus more. Comments are not checked.

3. **Code that will always fail to build (2 rules):**
   - `using FlaxEngine;` + `using System.Numerics;` + bare `Vector3` — the build doesn't know which `Vector3` you mean. Fix: `using Vector3 = FlaxEngine.Vector3;`
   - `??=` — fails on this project's build settings.

   These are blocked even if you checked before, because a check doesn't make the wrong name right.

In this free build the "does this type already exist somewhere else?" check is off when you don't have an inventory file, so it works in any project.

### Real example

```
1. flaxnav lookup Actor              -> found, good for Actor.cs
2. edit Actor.cs                     -> allowed (1 of 3)
3. edit Other.cs                     -> blocked (Other.cs needs its own check)
4. flaxnav search Other              -> found, good for Other.cs
5. edit Other.cs                     -> allowed
```

Install both files: `flaxmcp-nav.ts` gives you the tool and the 3-save rule, `cs-edit-gates.mjs` checks the file content. Use both for the full help.

## Requirements

- .NET 8 SDK
- Windows 10 or 11

## Install

From source:
```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# -> bin/Release/net8.0/flaxmcp-nav.exe
```

From Release:
Download `flax-nav-v2.3.0-with-plugin-win-x64.zip` from GitHub Releases. It has the exe, its dlls, `nav.ps1`, and `opencode-plugin/`.

## Quick start

```pwsh
# One time — no daemon needed, it builds the index on demand
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge maxResults=10
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/find_definition symbolName=Player
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics maxResults=5
.\bin\Release\net8.0\flaxmcp-nav.exe docs/find_section query=flax

# With helper (starts daemon if needed)
pwsh nav.ps1 csharp/symbol_search query=Bridge
pwsh nav.ps1 csharp/find_definition symbolName=Player

# Keep it running
flaxmcp-nav.exe --daemon
flaxmcp-nav.exe --health
flaxmcp-nav.exe --warm
```

## Install the OpenCode plugin

Copy the folder into your project:

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

Add to `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

Details are in `opencode-plugin/README.md`. To turn the block off for testing, set `FLAXMCP_NAV_GATE_DISABLE=1`.

## Help

- `flaxmcp-nav.exe --health` shows files, symbols, members, and if the watcher is on
- If searches get slow after a big build, the watcher overflowed — next search fixes it. Check the log for `InternalBufferOverflow`
- If Flax API says "xml not found", set `FLAXMCP_FLAX_XML` to the full path of your `FlaxEngine.CSharp.xml`

## License

MIT — Copyright (c) 2026 Flax Game Studio. See `LICENSE`.
