# FlaxNav OpenCode plugin

Stops you from saving broken `.cs` code. It blocks `edit` and `write` on `.cs` files until you check the Flax API with `flaxnav`.

## Why

Without this, AI (and sometimes you) writes a Flax name that doesn't exist, you save, the build fails, and the editor needs a restart. This checks first.

## How it helps

- **One check per file, lasts 30 minutes.** A check for `Player.cs` doesn't count for `Enemy.cs`. Each file needs its own.
- **Three saves per check.** After three `.cs` saves you need to check again. Stops "check once, guess many times."
- **Unity mistakes blocked.** `MonoBehaviour` -> `Script`, `GameObject` -> `Actor`, `GetComponent` -> `GetScript`/`GetChild`, `Rigidbody` -> `RigidBody`, `Time.deltaTime` -> `Time.DeltaTime`, `using UnityEngine` -> `FlaxEngine`, etc. (20 rules). Comments are not checked.
- **Always-broken code blocked.** Using `Vector3` when you imported both `FlaxEngine` and `System.Numerics` without saying which one, and `??=` which fails on this project's build.

In this free build you don't need an inventory file.

## What counts as a check

Any `flaxnav` call that searched real code and found something:

- `flax_api/lookup` — does this type exist?
- `flax_api/members_of` / `enum_values` — does this member or enum value exist?
- `csharp/find_definition` / `symbol_search` / `describe_symbol` — does this name exist in my project?

If it finds 0, it doesn't count.

When it blocks, it tells you what to call. For a member you need `members_of`, not just `lookup`.

## Install

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

In `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

The first file adds the `flaxnav` tool and the three-save rule. The second checks the file content. Use both.

## Use

```ts
// 1. Check first
await flaxnav({ atomic: "flax_api/lookup", args: { name: "Actor" } });
await flaxnav({ atomic: "csharp/symbol_search", args: { query: "MyType" } });

// 2. Now save — it works
await edit({ filePath: "Source/Game/MyScript.cs", oldString: "...", newString: "..." });

// 3. Fourth save without a new check -> blocked
// "too many saves (3) since last flaxnav, call flaxnav again"
```

## Turn off

Set `FLAXMCP_NAV_GATE_DISABLE=1` to disable.

## Files

- `flaxmcp-nav.ts` — tool + three-save rule
- `cs-edit-gates.mjs` — wrapper for the checks
- `lib/cs-edit-gates.core.mjs` — the checks
- `lib/flaxmcp-nav-gate.core.mjs` — counts saves
- `lib/unity-isms.core.mjs` — 20 Unity rules
- `lib/gate-shared.core.mjs`, `lib/behavior-synonyms.mjs`, `lib/flaxmcp-tool-aliases.core.mjs` — helpers
