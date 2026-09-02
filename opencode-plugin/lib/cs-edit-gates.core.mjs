// cs-edit-gates.mjs — OpenCode plugin: the merged .cs-edit enforcement gate.
//
// WHY THIS EXISTS (was two files): api-verify-gate.mjs enforced the "C# API-first
// law" — block .cs edits until a Flax API / repo symbol has been verified via
// flaxnav. reuse-verify-gate.mjs enforced repository reuse — block C# TYPE
// CREATION until a semantic repo query proves the capability doesn't already
// exist. Both were .cs-edit gate plugins sharing the FILE_EDIT_TOOLS surface,
// and together they were 29 of the 512 own-gate tool errors measured 2026-08-04
// (api-verify-gate 21, reuse-verify-gate 8). They are merged here into one
// plugin with one shared FILE_EDIT_TOOLS set and one combined C# preflight
// error, so API verification, repository reuse, and nav-window proof are shown
// together instead of being paid as sequential retries.
//
// Three-layer enforcement (kept from the originals):
//   1. tool.definition: injects both mandates into edit/write/apply_patch
//      descriptions (the LLM reads them every time it asks for the schema).
//   2. tool.execute.before: HARD block — throws one structured error naming
//      every missing nav/API/reuse check for a .cs edit/write.
//   3. tool.execute.after: tracks verification calls, reuse queries, and
//      inventory consultations as per-session proof.
//
// The error-message markers "API-VERIFY-GATE" and "REUSE-VERIFY-GATE" are
// preserved verbatim from the originals — the gate test suite matches on them.

// 2026-08-20: two more HARD blocks join this gate — Unity-code and
// guaranteed-compile-break. Both check the CONTENT being written, which this
// gate already had in hand (declaredTypesIn reads it) and never looked at.
//
// Operator, after a night of it: "OPENCODE MUSE MODEL I USE IGNORE ALL RULES
// AND WRITE UNITY CODE .. NOTHING BLOCKS HIM TOO" and "HE IGNORE OUR RULES
// TOTALLY!!! WE NEED BLOCK BEFORE HE EDIT". A model that ignores AGENTS.md
// ignores a paragraph; it does not ignore a refused write. Prompt mandates are
// the wrong instrument for a model that does not read them.
//
// These two are NEVER waived by a verification stamp. Having looked up a Flax
// type earlier does not make MonoBehaviour compile, and does not disambiguate
// Vector3. Rule tables + rationale: scripts/dev/lib/unity-isms.core.mjs.
import fs from "fs";
import {
  detectUnityIsms, buildUnityBlockMessage,
  detectCompileBreakers, buildCompileBreakerMessage,
} from "./unity-isms.core.mjs";
import {
  foldersForTerm,
  expandedTermsFor,
  foldersForTerms,
  SYNONYM_FAMILIES,
} from "./behavior-synonyms.mjs";

/** The C# text an edit/write/apply_patch call is about to commit. */
function writtenSourceOf(tool, args) {
  const parts = [];
  for (const k of ["content", "newString", "new_string", "patchText", "patch"]) {
    if (typeof args?.[k] === "string" && args[k]) parts.push(args[k]);
  }
  if (Array.isArray(args?.edits)) {
    for (const e of args.edits) {
      if (typeof e?.newString === "string") parts.push(e.newString);
      else if (typeof e?.new_string === "string") parts.push(e.new_string);
    }
  }
  return parts.join("\n");
}

// ── API verification tracking (per-session) ────────────────────────────────
// sessionID → { verified: true, timestamp }
// Verification is PER-SESSION (main + each subagent must verify independently).
const verifiedSessions = new Map();
const VERIFY_TIMEOUT_MS = 1_800_000; // 30 min — re-verify after this

// Tools (the direct plugin tool `flaxnav`, or the inner name of a
// flax_call / flax_flax_call MCP-alias call) that count as "API verification".
const VERIFY_INNER_TOOLS = new Set([
  "flaxnav",
  "nav/query",
  "flax_api/lookup",
  "flax_api/search",
  "flax_api/members_of",
  "flax_api/enum_values",
  "flax_api/inheritance_chain",
  "csharp/find_definition",
  "csharp/symbol_search",
  "csharp/find_references",
  "csharp/find_implementations",
  "csharp/describe_symbol",
  "csharp/index_health",
  "knowledge/lookup_api",
  "knowledge/api_search",
]);

// Verification is scoped to ONE FILE (2026-08-19).
//
// It used to be a single session-wide boolean with a 30-minute window: one
// nav call at any point authorized unlimited .cs writes to unlimited files for
// the next half hour. That is how `CombatHitKind.Impact` — a member that does
// not exist on that enum (Strike/Projectile/Explosion) — was written into two
// files and shipped as a CS0117 build break. `flax_api/enum_values` is in this
// gate's OWN accept-list and answers exactly that question, but the gate was
// already green from an unrelated earlier call, so it never asked.
//
// A verification now starts UNBOUND and binds to the first file it authorizes.
// Further edits to that same file reuse it (a multi-edit pass on one file is
// one piece of work); a DIFFERENT file needs its own verification. Proportionate
// to the actual risk — "am I writing against an API I have not checked" is a
// per-file question — and it cannot be satisfied by one call at session start.
function isVerified(sessionID, filePath) {
  const state = verifiedSessions.get(sessionID);
  if (!state || !state.verified) return false;
  if (Date.now() - state.timestamp > VERIFY_TIMEOUT_MS) {
    verifiedSessions.delete(sessionID);
    return false;
  }
  if (state.authorizedFile === null) return true;       // unbound: covers the next file
  if (typeof filePath !== "string") return true;        // no target to bind against
  return state.authorizedFile === normalizeFileKey(filePath);
}

function markVerified(sessionID) {
  verifiedSessions.set(sessionID, { verified: true, timestamp: Date.now(), authorizedFile: null });
}

/** Bind an unbound verification to the file it just authorized. */
function bindVerification(sessionID, filePath) {
  const state = verifiedSessions.get(sessionID);
  if (!state || state.authorizedFile !== null) return;
  if (typeof filePath !== "string" || filePath.length === 0) return;
  state.authorizedFile = normalizeFileKey(filePath);
  // Keep the path as the agent wrote it — the normalized key is lowercased,
  // and a block message naming "arenaenemyai.cs" reads like a different file.
  state.authorizedDisplay = filePath;
}

function normalizeFileKey(filePath) {
  return filePath.replace(/\\/g, "/").toLowerCase();
}

// ── Shared proof-signal matcher ────────────────────────────────────────────
// Single source of truth for "does this tool call count as API verification?".
// Exported so flaxmcp-nav.ts's C#-edit gate accepts the SAME signals instead
// of maintaining its own divergent list — the two gates must never drift apart
// on what counts as "verified". (flaxmcp-nav.ts imports this; keep the name.)
export function isVerificationCall(tool, args) {
  if (typeof tool !== "string" || tool.length === 0) return false;
  if (VERIFY_INNER_TOOLS.has(tool)) return true;
  const norm = normalizeToolName(tool);
  return (
    norm === "flax_call" &&
    args !== null && typeof args === "object" &&
    typeof args.name === "string" &&
    VERIFY_INNER_TOOLS.has(args.name)
  );
}

// ── Shared: every tool that can write a file, and its target paths ────────
//
// `apply_patch` carries its targets inside a `patchText` blob instead of a
// `filePath` argument — and it is named "apply_patch", not "patch". Every gate
// matching only {edit, write} had a hole exactly the size of this tool
// (measured 2026-08-04: 296 apply_patch calls in 4 days, 78 rewriting .cs with
// no verification). opencode.jsonc sets `"apply_patch": false` and it plainly
// does not take effect, so the tool cannot be assumed absent.
//
// Exported so no gate re-derives this and drifts again.
export const FILE_EDIT_TOOLS = new Set(["edit", "write", "patch", "apply_patch", "multiedit"]);

const PATCH_TARGET_RE = /^\s*\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*(.+?)\s*$/gm;

/**
 * All file paths a tool call will write, as an array (possibly empty).
 * Handles the {filePath} tools and apply_patch's embedded patch header.
 */
export function getEditTargetPaths(tool, args) {
  try {
    if (!args || typeof args !== "object") return [];
    if (typeof args.filePath === "string" && args.filePath.length > 0) return [args.filePath];
    if (typeof args.file_path === "string" && args.file_path.length > 0) return [args.file_path];

    const patchText = typeof args.patchText === "string" ? args.patchText
      : typeof args.patch === "string" ? args.patch
      : typeof args.input === "string" ? args.input
      : null;
    if (patchText === null) {
      return typeof args.path === "string" && args.path.length > 0 ? [args.path] : [];
    }
    const paths = [];
    PATCH_TARGET_RE.lastIndex = 0;
    let m;
    while ((m = PATCH_TARGET_RE.exec(patchText)) !== null) {
      if (m[1]) paths.push(m[1]);
    }
    return paths;
  } catch {
    return [];  // never crash a gate over arg parsing
  }
}

/** True when any path this call writes is a C# source file. */
export function touchesCSharp(tool, args) {
  return getEditTargetPaths(tool, args).some(p => p.toLowerCase().endsWith(".cs"));
}

// ── Shared failure sniffing ────────────────────────────────────────────────
// A legitimate query that returns count:0 (symbol genuinely doesn't exist) is
// a TRUE, USEFUL answer — it must count as verification/reuse proof. But a call
// that errored (unknown_tool, broken kernel, isError:true) must NOT mark the
// session verified. Unified across all gates in lib/gate-shared.core.mjs.
import { callFailed, normalizeToolName } from "./gate-shared.core.mjs";
import { takeNavGateProblem } from "./flaxmcp-nav-gate.core.mjs";

// ── Repository reuse proof (per-session, credit-bounded) ──────────────────
// One successful semantics repository query authorizes one C# type-creation
// operation. Absence evidence is stricter than the engine rule: a count:0
// result proves only that NO SYMBOL MATCHED THE QUERY TERM, not that the
// capability is missing — so a count:0 query (or ANY type created under
// Source/Game) additionally requires that the session consulted
// .agents/gameside-inventory.generated.md (the 787-type KNOW-WHAT-EXISTS
// surface). A failed query is never evidence.
const REPO_REUSE_ATOMICS = new Set([
  "csharp/symbol_search",
  "csharp/find_definition",
  "csharp/find_references",
  "csharp/find_implementations",
  "csharp/get_call_hierarchy",
  "csharp/describe_symbol",
]);

const PROOF_TIMEOUT_MS = 10 * 60 * 1000;
const reuseProofs = new Map();
const pendingConsumptions = new Map();
const inventoryConsultations = new Map();
// Exact folder-section evidence: which inventory folder headers have been
// consulted, per session. A plain `read` of gameside-inventory counts as
// file-level evidence (backward compat), but a section header like
// "## Shared/Gameplay" proves the agent inspected the right folder.
const inventorySectionsConsulted = new Map(); // sessionID → [{ section, timestamp }]
const SECTION_HEADER_RE = /^##\s+([^\s(]+)/gm;
// sessionID → [{ term, timestamp }] — WHAT each reuse query actually asked
// about, so a query that only echoed the type name the agent just invented can
// be recognised as the tautology it is. See isTautologicalReuseTerm.
const reuseQueryTerms = new Map();
const MAX_TRACKED_TERMS = 12;

function recordReuseTerm(sessionID, term) {
  if (typeof term !== "string" || term.trim().length === 0) return;
  const list = reuseQueryTerms.get(sessionID) ?? [];
  list.push({ term: term.trim(), timestamp: Date.now() });
  while (list.length > MAX_TRACKED_TERMS) list.shift();
  reuseQueryTerms.set(sessionID, list);
}

/** Reuse terms asked within the proof window, newest first. */
function freshReuseTerms(sessionID) {
  const list = reuseQueryTerms.get(sessionID);
  if (!Array.isArray(list)) return [];
  const cutoff = Date.now() - PROOF_TIMEOUT_MS;
  return list.filter(e => e.timestamp >= cutoff).map(e => e.term).reverse();
}

const INVENTORY_MARKERS = new Set([
  "gameside-inventory.generated.md",
  "content-inventory.generated.md",
]);

function hasFreshInventory(sessionID) {
  const stamp = inventoryConsultations.get(sessionID);
  if (!stamp) return false;
  if (Date.now() - stamp > PROOF_TIMEOUT_MS) {
    inventoryConsultations.delete(sessionID);
    return false;
  }
  return true;
}

export function isInventoryConsultation(tool, args) {
  if (args === null || typeof args !== "object") return false;
  if (tool === "read") {
    return typeof args.filePath === "string" && [...INVENTORY_MARKERS].some(marker => args.filePath.includes(marker));
  }
  if (tool === "grep") {
    const path = typeof args.path === "string" ? args.path : "";
    const pattern = typeof args.pattern === "string" ? args.pattern : "";
    return [...INVENTORY_MARKERS].some(marker => path.includes(marker) || pattern.includes(marker));
  }
  return false;
}

/**
 * Extract folder section headers from inventory text.
 * e.g. "## Shared/Gameplay (6)\n- `ArenaPlayerController`" → ["Shared/Gameplay"]
 */
export function parseInventorySections(text) {
  if (typeof text !== "string" || text.length === 0) return [];
  const sections = new Set();
  SECTION_HEADER_RE.lastIndex = 0;
  let m;
  while ((m = SECTION_HEADER_RE.exec(text)) !== null) {
    if (m[1]) sections.add(m[1].trim());
  }
  return [...sections];
}

/**
 * True when a read/grep explicitly targets a folder section header.
 * Detects patterns like "## Shared/Gameplay" in the grep pattern or in
 * the returned output text. Exact header match, not fuzzy.
 */
export function isFolderSectionConsultation(tool, args, outputText) {
  const pattern = typeof args?.pattern === "string" ? args.pattern : "";
  const filePath = typeof args?.filePath === "string" ? args.filePath
    : typeof args?.path === "string" ? args.path : "";
  const hasInventoryMarker = [...INVENTORY_MARKERS].some(marker => filePath.includes(marker) || pattern.includes(marker));
  if (!hasInventoryMarker) return false;
  // Pattern itself names a section: grep { pattern: "## Shared/Gameplay" }
  if (/^##\s+Shared\//.test(pattern)) return true;
  if (typeof outputText === "string" && /^##\s+Shared\//m.test(outputText)) {
    // Output contains a section header — parse to ensure at least one known section
    return parseInventorySections(outputText).length > 0;
  }
  // Grep pattern that matches a folder path fragment also counts when the
  // file is the inventory (e.g. grep pattern "Shared/Gameplay" within inventory)
  if (typeof pattern === "string" && /Shared\//.test(pattern)) return true;
  return false;
}

function getConsultedSections(sessionID) {
  const list = inventorySectionsConsulted.get(sessionID);
  if (!Array.isArray(list)) return [];
  const cutoff = Date.now() - PROOF_TIMEOUT_MS;
  return list.filter(e => e.timestamp >= cutoff).map(e => e.section);
}

export function hasRelevantFolderSection(sessionID, queryTerm) {
  const relevant = foldersForTerm(queryTerm);
  if (relevant.length === 0) return false;
  const consulted = getConsultedSections(sessionID);
  if (consulted.length === 0) return false;
  return consulted.some(sec => relevant.includes(sec));
}

function recordInventorySections(sessionID, tool, args, output) {
  try {
    const outputText = typeof output?.output === "string" ? output.output : "";
    const pattern = typeof args?.pattern === "string" ? args.pattern : "";
    // From grep pattern that names a section
    if (typeof pattern === "string" && pattern.length > 0) {
      const patternSections = parseInventorySections(pattern);
      // Also handle bare folder fragment in pattern (e.g. "Shared/Gameplay")
      if (patternSections.length === 0 && /Shared\//.test(pattern)) {
        const frag = pattern.match(/Shared\/[A-Za-z0-9/_-]+/g) ?? [];
        for (const f of frag) {
          const cleaned = f.replace(/[^A-Za-z0-9/_-].*$/, "");
          if (cleaned) {
            const list = inventorySectionsConsulted.get(sessionID) ?? [];
            list.push({ section: cleaned, timestamp: Date.now() });
            while (list.length > 50) list.shift();
            inventorySectionsConsulted.set(sessionID, list);
          }
        }
      } else {
        for (const sec of patternSections) {
          const list = inventorySectionsConsulted.get(sessionID) ?? [];
          list.push({ section: sec, timestamp: Date.now() });
          while (list.length > 50) list.shift();
          inventorySectionsConsulted.set(sessionID, list);
        }
      }
    }
    // From output text that contains headers
    if (typeof outputText === "string" && outputText.length > 0) {
      const outSections = parseInventorySections(outputText);
      for (const sec of outSections) {
        const list = inventorySectionsConsulted.get(sessionID) ?? [];
        list.push({ section: sec, timestamp: Date.now() });
        while (list.length > 50) list.shift();
        inventorySectionsConsulted.set(sessionID, list);
      }
    }
  } catch {
    // never crash a gate
  }
}

function isGamePath(filePath) {
  return typeof filePath === "string" &&
    /(^|[\\/])source[\\/]game([\\/]|$)/i.test(filePath);
}

function countIn(output) {
  const text = output?.output;
  if (typeof text !== "string" || text.length === 0) return null;
  try {
    const parsed = JSON.parse(text);
    return typeof parsed?.count === "number" ? parsed.count : null;
  } catch {
    return null;
  }
}

function repoAtomicOf(tool, args) {
  if (tool === "flaxnav") {
    return typeof args?.atomic === "string" ? args.atomic : null;
  }

  if (normalizeToolName(tool) !== "flax_call") return null;
  if (args === null || typeof args !== "object") return null;

  if (args.name === "nav/query") {
    return typeof args.arguments?.atomic === "string" ? args.arguments.atomic : null;
  }

  return typeof args.name === "string" ? args.name : null;
}

export function isRepositoryReuseProof(tool, args) {
  const atomic = repoAtomicOf(tool, args);
  return atomic !== null && REPO_REUSE_ATOMICS.has(atomic);
}

/**
 * The search term a repository-reuse query actually asked about.
 * Handles flaxnav {query|symbolName|name} and the nav/query nesting.
 */
export function reuseQueryTermOf(tool, args) {
  if (!args || typeof args !== "object") return null;
  const bag = (normalizeToolName(tool) === "flax_call")
    ? (args.arguments && typeof args.arguments === "object" ? args.arguments : args)
    : args;
  for (const key of ["query", "symbolName", "name", "pattern", "text"]) {
    const v = bag[key];
    if (typeof v === "string" && v.trim().length > 0) return v.trim();
  }
  return null;
}

/**
 * True when a reuse query only asked about the name the agent just invented.
 *
 * THE TAUTOLOGY THIS CLOSES (2026-08-19): the reuse gate fires on a new type
 * DECLARATION and accepts any repo query as proof. So an agent writes
 * `class ArenaEnemyAI`, searches "ArenaEnemyAI", gets the count:0 it was always
 * going to get for a name it invented thirty seconds ago, and proceeds. That is
 * a TRUE answer to the WRONG question: it asks "does this NAME exist", never
 * "does this BEHAVIOUR exist". 67k lines of game code — DismemberOnDeath,
 * CombatHitFeedHub, CombatDamage, MeleeSweepArc, RuntimeDismember, the complete
 * death/ragdoll/dismember chain — were invisible to it, and got reimplemented
 * ad hoc. The operator's summary was "67k code of game side no use?".
 *
 * A query is tautological when its term is just the created type's name (either
 * direction of containment, case-insensitive). At least one query must be about
 * something else — what the type DOES.
 */
export function isTautologicalReuseTerm(term, createdTypes) {
  if (typeof term !== "string" || term.length === 0) return false;
  const t = term.toLowerCase().replace(/[^a-z0-9]/g, "");
  if (t.length === 0) return false;
  return createdTypes.some((name) => {
    const n = String(name).toLowerCase().replace(/[^a-z0-9]/g, "");
    if (n.length === 0) return false;
    return t === n || t.includes(n) || n.includes(t);
  });
}

export function declaredTypesIn(source) {
  if (typeof source !== "string") return [];
  const names = new Set();
  const declaration = /(?:^|\r?\n)\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|ref|unsafe|new)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)\b/gm;
  for (const match of source.matchAll(declaration)) names.add(match[1]);
  return [...names];
}

function createdTypes(tool, args) {
  if (!args || typeof args !== "object") return [];

  // apply_patch carries both the removed and added lines in one blob. Added
  // lines start with "+"; comparing declarations found in added lines against
  // those in removed lines keeps a pure move/rename from reading as creation.
  if (typeof args.patchText === "string" || typeof args.patch === "string") {
    const blob = typeof args.patchText === "string" ? args.patchText : args.patch;
    const added = [];
    const removed = [];
    for (const line of blob.split(/\r?\n/)) {
      if (line.startsWith("+") && !line.startsWith("+++")) added.push(line.slice(1));
      else if (line.startsWith("-") && !line.startsWith("---")) removed.push(line.slice(1));
    }
    const existing = new Set(declaredTypesIn(removed.join("\n")));
    return declaredTypesIn(added.join("\n")).filter((name) => !existing.has(name));
  }

  const next = declaredTypesIn(tool === "write" ? args.content : args.newString);
  if (tool !== "edit") return next;

  const existing = new Set(declaredTypesIn(args.oldString));
  return next.filter((name) => !existing.has(name));
}

function isCSharpPath(args) {
  return touchesCSharp(null, args);
}

function hasFreshProof(sessionID, requireInventory) {
  const state = reuseProofs.get(sessionID);
  if (!state) return false;
  if (Date.now() - state.timestamp > PROOF_TIMEOUT_MS) {
    reuseProofs.delete(sessionID);
    return false;
  }
  if (state.credits <= 0) return false;
  if ((requireInventory || state.countZero) && !hasFreshInventory(sessionID)) {
    // Community: if no inventory file exists on disk, reuse is auto-pass (no .agents in standalone)
    try {
      if (!fs.existsSync(".agents/gameside-inventory.generated.md") && !fs.existsSync(".agents/content-inventory.generated.md")) return true;
    } catch {}
    return false;
  }
  return true;
}

// ── Mandates injected into tool descriptions ───────────────────────────────
const API_MANDATE =
  "CRITICAL RULE FOR .cs FILES: Before using {tool} for a .cs file, " +
  "you MUST call flax_api/lookup or csharp/find_definition first " +
  "to verify ALL Flax Engine API types and methods you reference. " +
  "This is CODE-ENFORCED — {verb} to .cs files will FAIL without prior API verification. " +
  'To satisfy: call flax_flax_call (flax_call) with name="flax_api/lookup" or name="csharp/find_definition".';

const REUSE_MANDATE =
  "CODE-ENFORCED (reuse-verify-gate): before creating a C# type, prove the " +
  "repository does not already provide the behavior. Call flaxnav with a C# " +
  "semantic query (normally csharp/symbol_search for the behavior and class " +
  "name), inspect the result, then reuse or extend an existing script/tool when " +
  "it fits. flax_api/lookup alone proves an engine API, not repository reuse. " +
  "A count:0 search is NOT sufficient absence evidence: it only proves the term " +
  "matched no symbol (the movement stack lives under ArenaPlayerController, not " +
  "\"WASD\"). After a count:0 result, and for any type created under Source/Game, " +
  "also read or grep .agents/gameside-inventory.generated.md (the 787-type " +
  "KNOW-WHAT-EXISTS surface) and inspect its folder sections before creating. " +
  "For asset-backed behavior, consult the generated content inventory and verify " +
  "the live tool/asset before choosing create; prefer configure, extend, or compose. " +
  "If the bridge or semantic index is unavailable, abstain — do not create from memory.";

/** @type {import("@opencode-ai/plugin").Plugin} */
const csEditGatesPlugin = async () => ({
  // ── Layer 1: inject both mandates into file-edit tool descriptions ──
  // Merge (append-if-missing), never overwrite: edit-prevalidator.mjs also
  // appends its own diagnostics text to the edit description. Whichever hook
  // runs first seeds the base, the others see their marker already present and
  // skip, so the final description carries them all in any hook order.
  "tool.definition": async (input, output) => {
    try {
      if (input.toolID !== "write" && input.toolID !== "edit" && input.toolID !== "apply_patch") return;

      if (input.toolID === "write") {
        // BOTH mandates, not just the API one. The 2026-08-04 merge of
        // api-verify-gate + reuse-verify-gate appended only API_MANDATE here and
        // returned early, silently dropping the reuse mandate from `write` — the
        // single tool the reuse gate exists for (it fired on write 7 times out of
        // 8; see gate-plugins-are-the-top-error-source.md). `edit`/`apply_patch`
        // below always carried both, which is why this went unnoticed.
        const api = API_MANDATE.replaceAll("{tool}", "write").replaceAll("{verb}", "writes");
        if (!output.description) {
          output.description = "Write content to a file.\n" + api + "\n" + REUSE_MANDATE;
          // Merged parameters — APPEND-ONLY, never replace the schema wholesale.
          // The old code assigned output.parameters = {...} while description was
          // empty, silently DROPPING any unknown keys or properties another hook
          // had already seeded on the same schema. Only default each key when it
          // is missing; leave everything else untouched.
          if (!output.parameters || typeof output.parameters !== "object") {
            output.parameters = { type: "object" };
          }
          if (!output.parameters.properties || typeof output.parameters.properties !== "object") {
            output.parameters.properties = {};
          }
          if (!output.parameters.properties.filePath) {
            output.parameters.properties.filePath = { type: "string", description: "The absolute path to the file to write" };
          }
          if (!output.parameters.properties.content) {
            output.parameters.properties.content = { type: "string", description: "The content to write to the file" };
          }
          // Never overwrite a caller-provided `required`; only seed the default
          // when the schema didn't carry one yet.
          if (!Array.isArray(output.parameters.required)) {
            output.parameters.required = [];
            if (!output.parameters.required.includes("filePath")) output.parameters.required.push("filePath");
            if (!output.parameters.required.includes("content")) output.parameters.required.push("content");
          }
        } else if (!output.description.includes("CODE-ENFORCED — writes to .cs files")) {
          output.description = output.description + "\n" + api + "\n" + REUSE_MANDATE;
        }
        return;
      }

      // edit (and apply_patch) — description-only merge; parameters predefined.
      if (output.description?.includes("CODE-ENFORCED — edits to .cs files") ||
          output.description?.includes("reuse-verify-gate")) {
        return; // mandates already present from a prior hook in this run
      }
      const base = output.description && output.description.trim().length > 0
        ? output.description
        : (input.toolID === "edit" ? "Edit an existing file by replacing text." : "Apply a patch to one or more files.");
      const api = API_MANDATE.replaceAll("{tool}", input.toolID).replaceAll("{verb}", "edits");
      output.description = base + "\n" + api + "\n" + REUSE_MANDATE;
    } catch {
      // Definition hints must never break tool registration.
    }
  },

  // ── Layer 2: HARD blocks on unguarded .cs edits ─────────────────
  // Nav, API, and reuse checks contribute to one preflight result.
  "tool.execute.before": async (input, output) => {
    const { tool, sessionID } = input;
    const args = output?.args;
    if (!FILE_EDIT_TOOLS.has(tool)) return;

    // Every check is EVALUATED before anything throws, and one error carries
    // every unmet requirement.
    //
    // 2026-08-04: the merge of api-verify-gate + reuse-verify-gate left the
    // preconditions as sequential `throw`s, so an unverified new-type write
    // reported only the API mandate; the agent satisfied it, retried, and was
    // blocked a second time by the reuse mandate it had never been shown. Two
    // block-fix-retry cycles for one call is the exact failure mode measured in
    // gate-plugins-are-the-top-error-source.md (110 of 326 gate blocks were
    // followed by an immediate identical retry, up to 6x, and weak models loop
    // hardest). Combining gates has to merge the MESSAGE, not just the file.
    const problems = [];
    const navProblem = takeNavGateProblem(output);
    if (navProblem) problems.push(navProblem.message);

    // Check A — API verification. Arm on ANY .cs target anywhere in the call
    // (apply_patch can rewrite several files; one .cs entry arms the gate).
    const csTargets = getEditTargetPaths(tool, args).filter(p => p.toLowerCase().endsWith(".cs"));

    // Checks U + C — Unity code, and writes certain to break the build.
    // Content-based, never waived by a verification stamp. See the import.
    if (csTargets.length > 0) {
      const written = writtenSourceOf(tool, args);
      const unity = detectUnityIsms(written);
      if (unity.length > 0) problems.push(buildUnityBlockMessage(csTargets[0], unity));
      const breakers = detectCompileBreakers(written);
      if (breakers.length > 0) problems.push(buildCompileBreakerMessage(csTargets[0], breakers));
    }

    if (csTargets.length > 0 && !isVerified(sessionID, csTargets[0])) {
      const boundState = verifiedSessions.get(sessionID);
      const bound = boundState?.authorizedDisplay ?? boundState?.authorizedFile;
      const scopeNote = bound
        ? "\nA verification IS active this session, but it is bound to a different file " +
          `(${bound}). Verification is per-file: one nav call no longer authorizes edits ` +
          "across the whole session. Verify the APIs THIS file uses.\n"
        : "";
      problems.push(
        "[API-VERIFY-GATE] BLOCKED: " + tool + " to .cs file \"" + csTargets[0] + "\" rejected.\n" +
        "You must verify Flax Engine API types before editing .cs files.\n" + scopeNote +
        "Call: flax_flax_call (shows as flax_call) with:\n" +
        '  name: "flax_api/lookup", arguments: { name: "<TypeName>" }\n' +
        "for each Flax Engine type you reference, or:\n" +
        '  name: "csharp/find_definition", arguments: { symbolName: "<Symbol>" }\n' +
        "for repo symbols.\n" +
        "MEMBERS AND ENUM CASES ARE NOT COVERED BY A TYPE LOOKUP. If you write " +
        "`SomeType.SomeMember`, verify the member exists: flax_api/members_of { name } " +
        "or flax_api/enum_values { name }. Writing a plausible-looking enum case that does " +
        "not exist is a CS0117 build break that then presents as mysterious runtime behaviour, " +
        "because the editor keeps running the last assembly that DID compile.\n" +
        "After verification succeeds (returns ok:true), retry the " + tool + "."
      );
    }

    // Check B — repository reuse proof on C# type creation.
    const types = isCSharpPath(args) ? createdTypes(tool, args) : [];
    const needsReuseProof = types.length > 0 &&
      !hasFreshProof(sessionID, isGamePath(args.filePath));
    if (needsReuseProof) {
      problems.push(
        `[REUSE-VERIFY-GATE] BLOCKED: creating C# type(s) ${types.join(", ")} without repository reuse evidence.\n` +
        "Before creating code, call flaxnav with csharp/symbol_search or another csharp/* semantic query " +
        "for the requested behavior and relevant type names. Inspect matching scripts/tools and reuse or extend them when they fit.\n" +
        "A count:0 result is NOT sufficient absence evidence — it only proves the query term matched no symbol. " +
        "After a count:0 result (and for ANY type under Source/Game), also read or grep " +
        ".agents/gameside-inventory.generated.md and inspect its folder sections for the existing capability.\n" +
        "For asset-backed behavior, also consult .agents/content-inventory.generated.md and verify the live tool/asset.\n" +
        "Prefer configure, extend, or compose over creating a new type. If the bridge or semantic index is unavailable, abstain.\n" +
        "flax_api/lookup alone does NOT satisfy this gate. " +
        "One successful repository query authorizes one type-creation operation; then retry this edit."
      );
    }

    // Check B2 — the reuse query must be about the BEHAVIOUR, not about the
    // name the agent just invented. Searching "ArenaEnemyAI" before creating
    // ArenaEnemyAI is guaranteed count:0 and proves nothing; it is a true
    // answer to the wrong question, and it is why the existing combat/death
    // stack was reimplemented instead of reused.
    if (types.length > 0 && !needsReuseProof) {
      const terms = freshReuseTerms(sessionID);
      const behaviourTerms = terms.filter(t => !isTautologicalReuseTerm(t, types));
      if (terms.length > 0 && behaviourTerms.length === 0) {
        problems.push(
          `[REUSE-VERIFY-GATE] BLOCKED: the only repository queries this session (${terms.join(", ")}) ` +
          `just echoed the name(s) you are creating (${types.join(", ")}).\n` +
          "A name you invented moments ago is ALWAYS count:0 — that result proves nothing about whether " +
          "the behaviour already exists. Ask what the type DOES, not what you called it.\n" +
          "Query the capability instead, e.g. csharp/symbol_search for the verbs and nouns of the behaviour " +
          '("enemy chase target", "melee strike damage", "ragdoll on death", "health component"), then read ' +
          "the matches before creating.\n" +
          "This gate exists because a full death/ragdoll/dismember chain (DismemberOnDeath, CombatHitFeedHub, " +
          "CombatDamage, MeleeSweepArc, RuntimeDismember) was already in the repo and got reimplemented ad hoc " +
          "behind a novel class name that sailed through a name-only search."
        );
      }
    }

    if (problems.length > 0) {
      throw new Error(
        problems.length === 1
          ? problems[0]
          : `${problems.length} gates blocked this ${tool}. Satisfy ALL before retrying — ` +
            `fixing only one will block again.\n\n${problems.join("\n\n")}`
      );
    }

    // The gate passed: bind this session's verification to the file it just
    // authorized, so the NEXT distinct .cs file needs its own verification.
    if (csTargets.length > 0) bindVerification(sessionID, csTargets[0]);

    if (types.length === 0) return;

    pendingConsumptions.set(sessionID, { tool, filePath: args.filePath });
  },

  // ── Layer 3: track proof signals (verification, reuse, inventory) ─
  "tool.execute.after": async (input, output) => {
    try {
      const { tool, sessionID } = input;
      const args = output?.args ?? input?.args ?? {};
      if (callFailed(output)) return;

      if (isInventoryConsultation(tool, args)) {
        inventoryConsultations.set(sessionID, Date.now());
        recordInventorySections(sessionID, tool, args, output);
        return;
      }

      if (isRepositoryReuseProof(tool, args)) {
        reuseProofs.set(sessionID, { credits: 1, timestamp: Date.now(), countZero: countIn(output) === 0 });
        recordReuseTerm(sessionID, reuseQueryTermOf(tool, args));
        // FALL THROUGH: a csharp/* atomic can be BOTH a repository-reuse proof
        // AND an API-verification signal. The old early `return` here swallowed
        // the verification half of a csharp/find_definition / symbol_search /
        // describe_symbol call (these atomics are in REPO_REUSE_ATOMICS AND in
        // VERIFY_INNER_TOOLS), so editing any .cs file after a perfectly valid
        // repo-symbol lookup still hit API-VERIFY-GATE forever. Both signals
        // must register on the same call. (Bug surfaced 2026-08-04: demo-voice
        // CS1574 fix blocked despite WavClip/AudioDsp verified via nav/query.)
      }

      if (isVerificationCall(tool, args)) {
        markVerified(sessionID);
        return;
      }

      const pending = pendingConsumptions.get(sessionID);
      if (!pending || pending.tool !== tool) return;
      const state = reuseProofs.get(sessionID);
      if (state) state.credits = 0;
      pendingConsumptions.delete(sessionID);
    } catch {
      // The gate must not hide an unrelated successful tool result.
    }
  },
});

export default csEditGatesPlugin;
