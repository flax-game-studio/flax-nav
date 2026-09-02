// gate-shared.core.mjs — shared helpers for the gate plugins.
//
// WHY THIS EXISTS: innerNameOf was copy-pasted verbatim into
// editor-lifecycle-gate.mjs (AGENTS.md-applied lifecycle bans) and
// visual-verify-gate.mjs (screenshot-assess gate), and callFailed existed as
// two near-identical sniffers in visual-verify-gate.mjs and
// cs-edit-gates.core.mjs. Three files, three copies, silently drifting apart
// on what "failed" means and which call shapes resolve to an inner name.
// This module is the single source of truth for both, plus a re-export of
// FILE_EDIT_TOOLS (owned by cs-edit-gates.core.mjs) so the file-edit surface
// stays a real dependency, not a third hand-rolled copy.
//
// This is a lib/*.core.mjs module (never in opencode.jsonc's `plugin` array,
// per tests/test-plugin-export-shape.mjs) — its named exports are import-only
// and its factory default is never invoked by the loader. Pinning the split:
// the plugin files import the helpers from here and stay default-export-only.

// ── Inner tool names ──────────────────────────────────────────────────────
// MCP tools surface with the server prefix ("flax_code/wait_for_compile"), and
// the flax_call family nests the inner name in args.name. Mirrored in the
// two gate files this replaces so all three call shapes are caught.
// NOTE: the editor-lifecycle-gate blocks the REMOVED compile tool name
// (code/start_compile, deleted 2026-08-08) defensively — this resolver is
// what lets the block see it across all three call shapes.
// Also NOTE: cs-edit-gates.core.mjs's repoAtomicOf is NOT merged here — it keeps
// its nav/query nesting branch (args.arguments.atomic) that plain innerNameOf
// must not assume.
const PLUGIN_CANONICAL = new Set([
  "flax_search", "flax_search_scoped", "flax_describe", "flax_call", "flax_ping",
  "flaxnav", "flax_tool_verify_registration", "tool_verify_registration",
]);

export function normalizeToolName(name) {
  if (typeof name !== "string" || name.length === 0) return name;
  let cur = name;
  while (cur.startsWith("flax_")) {
    if (PLUGIN_CANONICAL.has(cur)) break;
    const stripped = cur.slice("flax_".length);
    if (!stripped) break;
    if (PLUGIN_CANONICAL.has(stripped)) return stripped;
    if (!stripped.startsWith("flax_")) return stripped;
    cur = stripped;
  }
  return cur;
}

export function innerNameOf(tool, args) {
  if (typeof tool !== "string" || tool.length === 0) return null;
  const norm = normalizeToolName(tool);
  if (norm === "flax_call") {
    return (args !== null && typeof args === "object" && typeof args.name === "string")
      ? args.name
      : null;
  }
  if (norm.startsWith("flax_")) return norm.slice("flax_".length);
  return norm;
}

// ── Shared result-envelope unwrap ───────────────────────────────────────────
// The Flax bridge returns EVERY tool result wrapped in an envelope:
//   {data: {...real result...}, isError: false, errorCode: null, ...}
// plus opencode's own transport wrapper, which can nest a second level
// (data.data). A gate that sniffs result fields (assessError, assessment,
// voidFrame, renderMode, visualError, visualDescription, description, ...)
// at the JSON top level therefore NEVER sees them — the real payload is
// inside `data`. This normalizes the parsed result before any field sniff:
// unwrap `data` recursively while it holds an object, and leave plain
// (non-enveloped) payloads untouched so existing top-level behavior is
// preserved.
export function unwrapEnvelope(value) {
  let v = value;
  while (
    v !== null && typeof v === "object" && !Array.isArray(v) &&
    Object.prototype.hasOwnProperty.call(v, "data") &&
    v.data !== null && typeof v.data === "object"
  ) {
    v = v.data;
  }
  return v;
}

// ── Shared failure sniffing ────────────────────────────────────────────────
// Superset field set = visual-verify-gate's richer sniff (error, ok:false,
// isError, errorMessage, errorCode. A legacy query that returns count:0 is a
// TRUE, USEFUL answer — it must count as proof — but a call that ERRORED
// (isError:true, unknown_tool, broken kernel) must not. Unified here so a
// gate's notion of "the call failed" never changes between gates.
//
// Envelope-aware: failure fields can sit at the envelope top level
// ({"data":null,"isError":true,"errorCode":...}) OR inside the unwrapped
// data payload. Check the raw shape first (keeps plain non-enveloped results
// byte-for-byte identical to the old behavior), then the unwrapped payload.
function hasErrorFields(obj) {
  if (!obj || typeof obj !== "object") return false;
  if (Object.prototype.hasOwnProperty.call(obj, "error") && obj.error) return true;
  if (Object.prototype.hasOwnProperty.call(obj, "errorMessage") && obj.errorMessage) return true;
  if (Object.prototype.hasOwnProperty.call(obj, "errorCode") && obj.errorCode) return true;
  if (obj.ok === false) return true;
  if (obj.isError === true) return true;
  return false;
}

export function callFailed(output) {
  const text = output?.output;
  if (typeof text !== "string" || text.length === 0) return false;
  try {
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object") {
      if (hasErrorFields(parsed)) return true;
      const unwrapped = unwrapEnvelope(parsed);
      if (unwrapped !== parsed && hasErrorFields(unwrapped)) return true;
    }
  } catch {
    // Not JSON — plain-text tool output, not a failure signal.
  }
  return false;
}

// Every tool that can write a file. Owned by cs-edit-gates.core.mjs (and
// pinned by tests/test-gate-tool-registry-drift.mjs); re-exported here so a
// gate can build a derived surface from it WITHOUT hand-rolling a second copy.
import { FILE_EDIT_TOOLS } from "./cs-edit-gates.core.mjs";
export { FILE_EDIT_TOOLS };

// Default factory kept for loader / export-shape convention symmetry — never
// registered as a real plugin (lib modules must stay out of the plugin array).
export default async () => ({ innerNameOf, normalizeToolName, unwrapEnvelope, callFailed, FILE_EDIT_TOOLS });