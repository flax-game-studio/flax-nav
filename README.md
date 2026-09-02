# FlaxNav by Flax Game Studio

I built this for my own Flax work. It saved me a ton of time so I'm sharing it. Free, MIT. Works on its own.

It finds C# code and checks if a Flax name actually exists — without opening the editor.

## What it does

- Find code in your project — where a type is defined, where it's used, search for a name
- Check Flax API — does `Actor` exist? What does it have? What can this enum be?
- Search your markdown docs

## How it works

Small program that runs on Windows. Listens on `\\.\pipe\flaxmcp-nav`.

First time it reads all your `Source/**/*.cs` files and makes a list of every word and where it is. After that searches are instant, like 0.05-0.3s. If you change a file it only re-reads that file.

Flax API comes from `FlaxEngine.CSharp.xml`. About 16k names.

`nav.ps1` starts the program by itself if it's not running. You can also run `flaxmcp-nav.exe --daemon` to keep it on, `--health` to see if it's ready, `--warm` to load everything early.

Logs are at `%TEMP%\flaxmcp-nav\daemon.log`.

## The plugin that saved me most

In `opencode-plugin/`. It stops you from saving broken C#.

Flax and Unity look similar but aren't. The plugin blocks Unity code that can't work in Flax and tells you the Flax name instead:

`MonoBehaviour` -> `Script`, `GameObject` -> `Actor`, `Rigidbody` -> `RigidBody`, `Time.deltaTime` -> `Time.DeltaTime`, `using UnityEngine` -> `FlaxEngine`, etc. 20 of these. Comments are fine.

It also stops code that will always fail to build, like using `Vector3` when you imported both `FlaxEngine` and `System.Numerics` without saying which one. Fix is `using Vector3 = FlaxEngine.Vector3;`.

And it makes you check the API first. One check lets you save 3 files. Each file needs its own check. After 30 minutes you check again. If you try to save without checking it blocks you and says what to run, like `flaxnav lookup Actor`, then you save again and it works.

This alone stopped most of my wasted builds.

## Need

.NET 8, Windows 10/11

## Install

```pwsh
dotnet build flaxmcp-nav.csproj -c Release
# -> bin/Release/net8.0/flaxmcp-nav.exe
```
Or download the zip from Releases.

## Try it

```pwsh
.\bin\Release\net8.0\flaxmcp-nav.exe csharp/symbol_search query=Bridge
.\bin\Release\net8.0\flaxmcp-nav.exe flax_api/search query=Physics
pwsh nav.ps1 csharp/find_definition symbolName=Player
flaxmcp-nav.exe --health
```

## Plugin install

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

In `opencode.jsonc`:
```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

See `opencode-plugin/README.md` for more.

To turn the block off: `FLAXMCP_NAV_GATE_DISABLE=1`.

## License

MIT — Flax Game Studio
