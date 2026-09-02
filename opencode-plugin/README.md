# FlaxNav plugin

Stops you from saving broken `.cs`. Blocks `edit` until you check the Flax API with `flaxnav`.

## What it does

- One check per file, good for 30 minutes. One file's check doesn't count for another file.
- 3 saves per check. After 3 you need to check again.
- Blocks Unity code and tells you the Flax name (`MonoBehaviour` -> `Script` etc.)
- Blocks code that will always fail to build (like bare `Vector3` with two `using` lines)

## Install

```pwsh
Copy-Item -Recurse opencode-plugin .opencode/plugin/flax-nav -Force
```
```jsonc
"plugin": ["./.opencode/plugin/flax-nav/flaxmcp-nav.ts", "./.opencode/plugin/flax-nav/cs-edit-gates.mjs"]
```

First file is the tool + 3-save rule, second checks the content. Use both.

## Use

```ts
await flaxnav({ atomic: "flax_api/lookup", args: { name: "Actor" } });
await edit({ filePath: "Source/Game/MyScript.cs", oldString: "...", newString: "..." });
// 4th save without new check -> blocked, says what to call
```

For a member use `members_of`, for enum use `enum_values`, not just `lookup`.

Turn off: `FLAXMCP_NAV_GATE_DISABLE=1`
