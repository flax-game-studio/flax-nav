// flaxmcp-tool-aliases.core.mjs ΓÇö shared helpers for
// flaxmcp-tool-aliases.mjs (NOT a plugin; never listed in the plugin array).
//
// WHY THIS FILE EXISTS: OpenCode's plugin loader calls EVERY named export of
// a registered plugin file as its own plugin factory. flaxmcp-tool-aliases.mjs
// exported detectCompositeSceneBuild for its tests, so the loader invoked it
// with a plugin input object on every boot ΓÇö the same export-shape bug that
// killed brain-memory for a week (682 boot failures, see brain-memory.mjs
// header). Test-only helpers live here so the registered plugin exports ONLY
// default. Pinned by tests/test-plugin-export-shape.mjs.

// ΓöÇΓöÇ Composite scene-build intent detection (2026-08-04, plan #19) ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ
// Weak models trust next:"call" whenever the top score clears the 9.0 bar
// (COMPOSITE_CALL_BAR below), even for COMPOSITE, human-worded intents no
// single tool can satisfy.
// Measured: "make a room with a desk and a chair in it" ranked a
// motion-classification router at 7.5 with next:"call". The fix is
// decomposition, not better search: composite phrasing with a top score
// below the bar yields a decompose-first warning instead of a confident
// call, pointing at the flax-scene-workflow skill's 13-step build order.
//
// Precision rules (minimal over-block): must name a space/container AND a
// build/place verb AND show evidence of multiple parts (a containment clause
// like "with ... in it", or an enumerated object list like "desk and chair").
// "build a navmesh" (no space word), "make a room" (no parts listed) and
// "put a chair in the room" (single object) stay on the normal path.
/** Score below which a composite intent becomes decompose-first (exported for the plugin's override). */
export const COMPOSITE_CALL_BAR = 9.0;const COMPOSITE_SPACE_RE = /\b(room|scene|house|building|level|dungeon|arena|kitchen|office|bedroom|bathroom|garage|apartment|cabin|garden|warehouse|workshop|studio|city)\b/i;
const COMPOSITE_BUILD_VERB_RE = /\b(make|build|create|put|place|set\s*up|furnish|fill|arrange|design|construct|assemble|add)\b/i;
const COMPOSITE_CONTAIN_RE = /\b(with|containing|inside|in it|including|full of|made of)\b/i;
const COMPOSITE_ENUM_RE = /\b\w+ and \w+\b/i;

/**
 * True when the intent asks to build/place multiple parts inside a named
 * space ("make a room with a desk and a chair in it"). Exported for tests.
 */
export function detectCompositeSceneBuild(intent) {
  if (typeof intent !== "string" || intent.trim().length === 0) return false;
  if (!COMPOSITE_SPACE_RE.test(intent)) return false;
  if (!COMPOSITE_BUILD_VERB_RE.test(intent)) return false;
  return COMPOSITE_CONTAIN_RE.test(intent) || COMPOSITE_ENUM_RE.test(intent);
}

// ΓöÇΓöÇ flaxnav count:0 terminal-note injection (2026-08-05, batch-2 B4) ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ
// Pure helper shared by flaxmcp-nav.ts (the real callsite) and the drift test
// so the terminal-note path has coverage. Kept HERE (dependency-free, no
// @opencode-ai/plugin import) precisely so the .ts plugin's imports stay
// transpile-only and the drift test can import it under bare `tsx` without
// resolving opencode's runtime plugin types.
//
// retryable / retry VOCABULARY SEAM (single source of truth): the daemon
// writes TWO spellings ΓÇö Program.Dispatch.cs B5b marks a terminal count:0 as
// `retry:false` (+ terminal:true, reason, segmentSearched, indexScope);
// Program.AcceptPump / Program.Warmup mark the transient warmup count as
// `retryable:true`. This gate checks ONLY `parsed.retryable !== true`. The
// daemon's count:0 emits `retry` (never `retryable`), so undefined !== true ΓåÆ
// the note is injected; warmup's retryable:true suppresses it. Every daemon
// field (retry, terminal, reason, segmentSearched, indexScope) survives the
// round-trip because injection only adds terminalNote before stringifying the
// same object.
/**
 * Feed one flaxnav daemon response line through the terminal count:0 gate.
 * Injects a terminalNote exactly when the response is a genuine count:0 (a
 * numeric `count` of 0) AND NOT retryable (warmup cold-start counts carry
 * retryable:true and get no prohibition). Returns the string unchanged
 * otherwise (non-JSON, no count field, count !== 0, or retryable). Exported
 * for tests; flaxmcp-nav.ts uses it at its execute-site.
 */
export function injectCountZeroTerminalNote(result) {
  try {
    const parsed = JSON.parse(result);
    if (parsed && typeof parsed === "object" && parsed.count === 0
        && parsed.retryable !== true) {
      parsed.terminalNote =
        "count:0 is a TERMINAL result ΓÇö this symbol genuinely does not exist " +
        "in the indexed source. Do NOT retry with a variant spelling, and do NOT " +
        "fall back to grep for this fact.";
      return JSON.stringify(parsed);
    }
  } catch {
    // Not JSON, or no count field ΓÇö return the raw result unchanged.
  }
  return result;
}

/** Human-readable decompose-first guidance for a composite intent. */
export function decomposeFirstWhy(intent, selectedTool, topScore) {
  const toolPart = selectedTool
    ? `Top match '${selectedTool}' (score ${topScore.toFixed(2)}) covers only ONE piece of this job.`
    : `The top match (score ${topScore.toFixed(2)}) covers only ONE piece of this job.`;
  return `'${intent}' is a composite scene-build intent ΓÇö no single tool builds it. ` +
    `${toolPart} Decompose first: follow the flax-scene-workflow skill's 13-step ` +
    `build order (scene/brief, then spawn/place each object, then ` +
    `scene/spatial_report), calling tools for one discrete operation at a time.`;
}
