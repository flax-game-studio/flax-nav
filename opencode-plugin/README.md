# FlaxNav OpenCode Plugin

Stops you from saving wrong `.cs` code. Blocks `edit` and `write` on `.cs` files until you check the real Flax API with `flaxnav`.

## What it does

- **Check per file (30 minutes):** One check lets you edit the next `.cs` file only. Another file needs its own check. After 30 minutes you need to check again.
- **3 saves per check:** Each `flaxnav` check gives you 3 `.cs` saves. After 3 you must check again. Stops "check once then guess many times".
- **Unity mistakes:** Stops Unity-only code like `MonoBehaviour`, `GameObject`, `GetComponent`, `Rigidbody` vs `RigidBody`, `Time.deltaTime` vs `Time.DeltaTime`, `using UnityEngine` and tells you the Flax name to use.
- **Broken code:** Stops code that will always fail to build, like using `Vector3` when you imported both `FlaxEngine` and `System.Numerics` without saying which one, and `??=` which broke this project.

In this free build you do not need an inventory file. That check is off.

## Install

Copy the `opencode-plugin/` folder to your project:

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```

Add to `opencode.jsonc`:

```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

`flaxmcp-nav.ts` gives you the `flaxnav` tool and the 3-save rule. `cs-edit-gates.mjs` checks the file per file and for Unity/broken code. Use both.

## How to use

```ts
// 1. Check that the name exists
await flaxnav({ atomic: "flax_api/lookup", args: { name: "Actor" } });
await flaxnav({ atomic: "csharp/symbol_search", args: { query: "MyType" } });

// 2. Now save .cs — it works
await edit({ filePath: "Source/Game/MyScript.cs", oldString: "...", newString: "..." });

// 3. 4th save without a new check -> blocked
// "too many saves (3) since last flaxnav, call flaxnav again"
```

If it blocks, it tells you what to call, then try again. For a member or enum value you need `flax_api/members_of` or `enum_values`, not just `lookup`.

## Turn off for testing

Set `FLAXMCP_NAV_GATE_DISABLE=1`.

## Files

- `flaxmcp-nav.ts` — tool + 3-save rule
- `cs-edit-gates.mjs` — wrapper for the checks
- `lib/cs-edit-gates.core.mjs` — the checks
- `lib/flaxmcp-nav-gate.core.mjs` — counts saves
- `lib/unity-isms.core.mjs` — 20 Unity rules
- `lib/gate-shared.core.mjs` — small helpers
- `lib/behavior-synonyms.mjs` — for reuse check
- `lib/flaxmcp-tool-aliases.core.mjs` — for messages
