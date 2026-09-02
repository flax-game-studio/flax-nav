// [domain: Unity-ism detection for Flax game C#]
//
// WHY THIS EXISTS
//   Flax and Unity look similar enough that a model trained mostly on Unity
//   will write Unity C# into this repo and nothing will stop it. The operator
//   hit exactly that on 2026-08-20: "OPENCODE MUSE MODEL I USE IGNORE ALL
//   RULES AND WRITE UNITY CODE .. NOTHING BLOCKS HIM TOO".
//
//   Prompt rules do not fix this. A model that ignores AGENTS.md ignores a
//   paragraph telling it to write Flax; it does not ignore a hook that refuses
//   the write. So this is a machine gate, and it names the Flax replacement in
//   the refusal so the retry is one edit, not a search.
//
// PRECISION OVER RECALL ΓÇö THE HARD PART
//   Flax and Unity SHARE many names, and blocking a shared name would refuse
//   correct code. Every one of these is a real Flax API and must NEVER be
//   flagged:
//       Vector3, Quaternion, Color, Mathf, Debug.Log, Input.GetAxis,
//       OnTriggerEnter, Time (the type), Camera (the type), Script
//   So the list below is deliberately narrow: each entry is either a symbol
//   Flax does not have at all, or one where the CASING differs (Rigidbody vs
//   Flax's RigidBody, Time.deltaTime vs Flax's Time.DeltaTime). Casing is a
//   reliable signal precisely because C# is case-sensitive ΓÇö the Unity
//   spelling cannot compile against Flax, so flagging it can never be wrong.
//
//   A false positive here blocks correct work, which is worse than missing
//   one Unity-ism. When in doubt, leave it out.

/**
 * @typedef {{ pattern: RegExp, unity: string, flax: string }} UnityIsm
 */

/** @type {UnityIsm[]} */
const UNITY_ISMS = [
  // Namespaces ΓÇö definitive, cannot exist in a Flax project.
  { pattern: /\busing\s+UnityEngine\b/, unity: "using UnityEngine", flax: "using FlaxEngine;" },
  { pattern: /\busing\s+UnityEditor\b/, unity: "using UnityEditor", flax: "using FlaxEditor; (editor-only code lives in a plugin, not in Source/Game)" },
  { pattern: /\bUnityEngine\s*\./, unity: "UnityEngine.*", flax: "FlaxEngine.*" },

  // Base types ΓÇö Flax has no equivalent name.
  { pattern: /\bMonoBehaviour\b/, unity: "MonoBehaviour", flax: "Script  (public class Foo : Script)" },
  { pattern: /\bScriptableObject\b/, unity: "ScriptableObject", flax: "JsonAsset  (see FlaxEngine.Json / [Serializable] POCO + Content.Load<JsonAsset>)" },

  // GameObject/Component model ΓÇö Flax uses Actor + Script.
  { pattern: /\bGameObject\b/, unity: "GameObject", flax: "Actor" },
  { pattern: /\bgameObject\b/, unity: "gameObject", flax: "Actor" },
  { pattern: /\bGetComponent\s*</, unity: "GetComponent<T>()", flax: "Actor.GetScript<T>() for scripts, Actor.GetChild<T>() for child actors" },
  { pattern: /\bAddComponent\s*</, unity: "AddComponent<T>()", flax: "Actor.AddScript<T>()" },
  { pattern: /\bTryGetComponent\b/, unity: "TryGetComponent", flax: "Actor.GetScript<T>() and null-check the result" },

  // Lifecycle helpers.
  { pattern: /\bInstantiate\s*\(/, unity: "Instantiate(...)", flax: "PrefabManager.SpawnPrefab(prefab, position)" },
  { pattern: /\bDestroyImmediate\s*\(/, unity: "DestroyImmediate(...)", flax: "Actor.Destroy() / Destroy(actor)" },

  // Case-differs traps ΓÇö the Unity spelling cannot compile here.
  { pattern: /\bRigidbody\b/, unity: "Rigidbody (lowercase b)", flax: "RigidBody (capital B) ΓÇö Flax spells it differently" },
  { pattern: /\bTime\s*\.\s*deltaTime\b/, unity: "Time.deltaTime", flax: "Time.DeltaTime (capital D)" },
  { pattern: /\bTime\s*\.\s*fixedDeltaTime\b/, unity: "Time.fixedDeltaTime", flax: "Time.DeltaTime inside OnFixedUpdate" },
  { pattern: /\bCamera\s*\.\s*main\b/, unity: "Camera.main", flax: "Camera.MainCamera" },

  // Serialization attribute.
  { pattern: /\[\s*SerializeField\s*[\],]/, unity: "[SerializeField]", flax: "[Serialize]" },

  // Coroutines ΓÇö WaitForSeconds is Unity-only; Flax coroutines differ.
  { pattern: /\bWaitForSeconds\b/, unity: "WaitForSeconds", flax: "Flax coroutines (Actor.StartCoroutine / Game.Shared Coroutines helpers) ΓÇö check the inventory before writing a new one" },
  { pattern: /\bStartCoroutine\s*\(/, unity: "StartCoroutine(...)", flax: "Flax's own coroutine surface ΓÇö consult .agents/gameside-inventory.generated.md" },

  // transform.* ΓÇö Unity's lowercase component accessor.
  { pattern: /\btransform\s*\.\s*(position|rotation|localPosition|localScale|eulerAngles)\b/, unity: "transform.position / .rotation / ...", flax: "Actor.Position, Actor.Orientation, Actor.LocalPosition, Actor.Scale" },
];

/**
 * Find Unity-only constructs in C# source.
 *
 * @param {string} source C# text about to be written.
 * @returns {{ unity: string, flax: string, line: number, text: string }[]}
 *   One finding per distinct Unity-ism, with the first line it appears on.
 */
export function detectUnityIsms(source) {
  if (typeof source !== "string" || source.length === 0) return [];

  const lines = source.split(/\r?\n/);
  const found = [];

  for (const ism of UNITY_ISMS) {
    for (let i = 0; i < lines.length; i++) {
      const raw = lines[i];
      // Skip comment-only lines: naming Unity in a comment (as this repo's own
      // docs do constantly ΓÇö "Flax spells it RigidBody, not Unity's Rigidbody")
      // must not block a write.
      const trimmed = raw.trim();
      if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*")) continue;

      if (ism.pattern.test(raw)) {
        found.push({ unity: ism.unity, flax: ism.flax, line: i + 1, text: trimmed.slice(0, 120) });
        break; // one finding per ism ΓÇö a list of 40 identical hits helps nobody
      }
    }
  }

  return found;
}

/**
 * Human-readable refusal naming every Unity-ism and its Flax replacement.
 *
 * @param {string} filePath
 * @param {ReturnType<typeof detectUnityIsms>} findings
 * @returns {string}
 */
export function buildUnityBlockMessage(filePath, findings) {
  const lines = [
    "[UNITY-CODE-GATE] Blocked ΓÇö this is Flax Engine, not Unity.",
    "",
    `File: ${filePath}`,
    "",
    "Unity-only constructs found (each cannot compile against FlaxEngine):",
  ];
  for (const f of findings) {
    lines.push(`  line ${f.line}: ${f.unity}`);
    lines.push(`      -> use: ${f.flax}`);
    lines.push(`      ${f.text}`);
  }
  lines.push("");
  lines.push(
    "Do NOT translate from memory ΓÇö Flax's API differs in ways that look right and are not.",
    "Verify each replacement first:",
    '  mcp__flax__flax_call { name: "nav/query", arguments: { atomic: "flax_api/lookup", args: { name: "<Type>" } } }',
    '  mcp__flax__flax_call { name: "nav/query", arguments: { atomic: "flax_api/members_of", args: { name: "<Type>" } } }',
    "",
    "Flax lifecycle on Script is OnStart / OnEnable / OnUpdate / OnFixedUpdate /",
    "OnLateUpdate / OnDisable / OnDestroy ΓÇö all `public override void`, not bare",
    "Start() / Update() / Awake().",
  );
  return lines.join("\n");
}

/** Exposed for tests and for anyone auditing what the gate actually blocks. */
export const UNITY_ISM_COUNT = UNITY_ISMS.length;

// ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ
// Guaranteed compile breaks
//
// Separate from Unity-isms: this is Flax-legal-looking code that CANNOT
// compile in this project, for reasons a model cannot see from the file it is
// writing. Blocking pre-write beats a failed build, because a failed game
// build takes the whole editor's scripting down ΓÇö and with it the MCP bridge,
// which is how the agent would have found out.
//
// 2026-08-20: MathPlayground.cs was written three times by three separate
// attempts, each time with `using FlaxEngine;` + `using System.Numerics;` and
// a bare `Vector3`. Every time: CS0104, Game.CSharp.dll fails, editor runs a
// stale assembly. Game.Build.cs adds System.Numerics.Vectors as a
// SystemReference (the pure-CLR math libraries need it), so BOTH Vector3 types
// are always in scope in this project ΓÇö the ambiguity is structural here, not
// occasional.
// ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ

/**
 * Detect writes that are certain to fail the Flax game build.
 *
 * @param {string} source
 * @returns {{ rule: string, why: string, fix: string, line: number }[]}
 */
export function detectCompileBreakers(source) {
  if (typeof source !== "string" || source.length === 0) return [];
  const lines = source.split(/\r?\n/);
  const code = lines.filter((l) => {
    const t = l.trim();
    return !(t.startsWith("//") || t.startsWith("*") || t.startsWith("/*"));
  });
  const joined = code.join("\n");
  const out = [];

  const usesFlax = /^\s*using\s+FlaxEngine\s*;/m.test(joined);
  const usesNumerics = /^\s*using\s+System\.Numerics\s*;/m.test(joined);
  const hasAlias = /^\s*using\s+\w+\s*=\s*(System\.Numerics|FlaxEngine)\.\w+\s*;/m.test(joined);

  if (usesFlax && usesNumerics && !hasAlias) {
    // Only a problem if an unqualified shared type is actually used.
    const sharedTypes = ["Vector2", "Vector3", "Vector4", "Quaternion", "Matrix4x4", "Plane"];
    for (const t of sharedTypes) {
      const bare = new RegExp(`(?<![.\\w])${t}\\b`);
      const idx = code.findIndex((l) => bare.test(l));
      if (idx >= 0) {
        out.push({
          rule: "ambiguous_" + t.toLowerCase(),
          why:
            `'${t}' is ambiguous: this file imports BOTH FlaxEngine and System.Numerics, ` +
            `and both define ${t}. This is CS0104 and it fails the whole Game.CSharp build.`,
          fix:
            `Pick one explicitly. Either drop 'using System.Numerics;' and use FlaxEngine.${t}, ` +
            `or alias it at the top: 'using ${t} = System.Numerics.${t};' (or ` +
            `'using ${t} = FlaxEngine.${t};'). Note Game.Build.cs adds System.Numerics.Vectors ` +
            `as a SystemReference for the pure-CLR math libraries, so both are ALWAYS in scope here.`,
          line: idx + 1,
        });
        break; // one is enough; the fix is the same for all of them
      }
    }
  }

  // `??=` has repeatedly failed this project's game-script compile with
  // "CS1002: ; expected" even though the generated csproj advertises a modern
  // LangVersion. Whatever the cause, the observed result is a broken build, so
  // do not write it in game scripts.
  {
    const idx = code.findIndex((l) => /\?\?=/.test(l));
    if (idx >= 0) {
      out.push({
        rule: "null_coalescing_assign",
        why:
          "'??=' has failed this project's game-script compile with CS1002 (; expected) ΓÇö " +
          "observed live on ArenaAudio.cs 2026-08-20, taking the whole Game module down.",
        fix: "Write it out: `if (X == null) X = ...;`",
        line: idx + 1,
      });
    }
  }

  return out;
}

/**
 * Refusal text for compile-breaker findings.
 *
 * @param {string} filePath
 * @param {ReturnType<typeof detectCompileBreakers>} findings
 */
export function buildCompileBreakerMessage(filePath, findings) {
  const lines = [
    "[COMPILE-BREAK-GATE] Blocked ΓÇö this write would fail the Flax game build.",
    "",
    `File: ${filePath}`,
    "",
    "A broken Game.CSharp build takes the editor's whole scripting layer down,",
    "and the MCP bridge with it ΓÇö so this is refused before the write, not after.",
    "",
  ];
  for (const f of findings) {
    lines.push(`  line ${f.line}: ${f.rule}`);
    lines.push(`      ${f.why}`);
    lines.push(`      FIX: ${f.fix}`);
  }
  return lines.join("\n");
}
