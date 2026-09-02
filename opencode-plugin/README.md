# FlaxNav OpenCode Plugin — API-verify gate

Blocks `edit`/`write`/`apply_patch` on `.cs` files until you verify the real Flax API via `flaxnav`. Saves compilation fails (`CS0117`, `CS0246`, stale-assembly confusion).

## What it does

- **Per-file API verification:** one `flaxnav` (or `flax_api/*` / `csharp/*`) call authorizes edits to the *next* `.cs` file only. A different file needs its own verification. 30-minute timeout.
- **3-edit window (nav gate):** each `flaxnav` call unblocks the next 3 `.cs` edits (`MAX_EDITS_PER_NAV=3`). After that you must verify again. Prevents "one lookup then guess forever".
- **Unity-ism blocking:** refuses writes containing Unity-only constructs (`MonoBehaviour`, `GameObject`, `GetComponent<T>`, `Rigidbody` vs `RigidBody`, `Time.deltaTime` vs `Time.DeltaTime`, `using UnityEngine`, etc.) and names the Flax replacement inline.
- **Compile-breaker blocking:** refuses the ambiguous `Vector3`/`Quaternion` when both `FlaxEngine` and `System.Numerics` are imported without an alias (`CS0104`), and `??=` which has broken this project's build.

No inventory required in community builds — the reuse gate auto-passes when `.agents/gameside-inventory.generated.md` is absent.

## Install

Copy the `opencode-plugin/` folder into your project:

```pwsh
Copy-Item -Recurse tools/flax-nav-dist/opencode-plugin .opencode/plugin/flax-nav-opencode -Force
```

Or add to `opencode.jsonc`:

```jsonc
{
  "plugin": [
    "./opencode-plugin/flaxmcp-nav.ts",
    "./opencode-plugin/cs-edit-gates.mjs"
  ]
}
```

`flaxmcp-nav.ts` provides the `flaxnav` tool + the 3-edit nav gate. `cs-edit-gates.mjs` adds the API-verify + Unity-ism + compile-breaker gates (composed via `command-guards.mjs` in the factory). Recommend both — `flaxmcp-nav.ts` alone covers the tool, `cs-edit-gates.mjs` covers the content checks.

Single composited alternative (factory `command-guards.mjs` composes all gates, but community keeps them separate for clarity):

```jsonc
{ "plugin": ["./opencode-plugin/flaxmcp-nav.ts"] }
```

already blocks on missing `flaxnav`; add `cs-edit-gates.mjs` for the content gates.

## Usage

```ts
// 1. Verify API exists (any of these counts)
await flaxnav({ atomic: "flax_api/lookup", args: { name: "Actor" } });
await flaxnav({ atomic: "csharp/symbol_search", args: { query: "MyType" } });

// 2. Now edit .cs — gate passes
await edit({ filePath: "Source/Game/MyScript.cs", oldString: "...", newString: "..." });

// 3. Fourth .cs edit without re-verifying → blocked with fix hint
// flaxnav-gate: blocked edit on 'Foo.cs' — too many .cs edits (3) since your last flaxnav call.
```

Error text tells the fix: call `flaxnav` with the correct atomic/args, then retry. Members and enum cases need `flax_api/members_of` / `flax_api/enum_values`, not just `lookup`.

## Escape hatch

Operator only: `FLAXMCP_NAV_GATE_DISABLE=1` disables the nav gate.

## Files

| File | Purpose |
|---|---|
| `flaxmcp-nav.ts` | `flaxnav` tool + `MAX_EDITS_PER_NAV=3` gate |
| `cs-edit-gates.mjs` | thin wrapper re-exporting `lib/cs-edit-gates.core.mjs` |
| `lib/cs-edit-gates.core.mjs` | API-verify + Unity-ism + compile-breaker logic (patched for community: `fs.existsSync` inventory guard, local `./unity-isms.core.mjs` import) |
| `lib/flaxmcp-nav-gate.core.mjs` | 3-edit window state |
| `lib/gate-shared.core.mjs` | shared `innerNameOf`/`callFailed` helpers |
| `lib/behavior-synonyms.mjs` | synonym → folder map for reuse gate |
| `lib/unity-isms.core.mjs` | 20 Unity rules + compile-breaker rules (restored from `475568d1e`) |
| `lib/flaxmcp-tool-aliases.core.mjs` | `injectCountZeroTerminalNote` + composite detection |
