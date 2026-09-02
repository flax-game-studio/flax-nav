// behavior-synonyms.mjs — auditable behavior → folder routing for reuse verification.
//
// WHY THIS EXISTS
//   Literal csharp/symbol_search queries miss canonical names. The movement stack
//   lives under ArenaPlayerController, not "WASD"; grapple lives in Shared/Rope
//   as GrappleController/HookGunController/VerletRopeSimulator, not "hook gun";
//   health damage lives in Shared/Combat as CombatDamage/CombatDamageResolver. A
//   count:0 answer is true for the LITERAL term but false as proof of absence.
//   Agents must retry under a synonym and inspect the right inventory folder.
//   This table is the machine-readable synonym → folder map that the gate and
//   the tool description point at. It is DATA, not a fuzzy engine.
//
// DESIGN CONSTRAINTS (contract)
//   - Data-driven / auditable: plain array of families, no embeddings/Levenshtein.
//   - Bounded: ≤10 families, ≤6 terms per family, ≤6 folders per family.
//   - Exact folder names must match .agents/gameside-inventory.generated.md
//     section headers (e.g. "Shared/Gameplay", "Shared/Character") so section
//     evidence is exact text match, not fuzzy.
//   - No fuzzy semantic engine: exact case-insensitive term equality only.

export const MAX_FAMILIES = 10;
export const MAX_TERMS_PER_FAMILY = 8;
export const MAX_FOLDERS_PER_FAMILY = 6;
export const MAX_EXPANDED_TERMS = 16;

/**
 * Each family maps a set of synonymous terms (all lowercased) to the
 * inventory folder sections where that behaviour lives. Folder strings are
 * verbatim suffixes of gameside-inventory headers: "## Shared/<Folder> (N)".
 */
export const SYNONYM_FAMILIES = [
  {
    id: "movement",
    // WASD is the canonical miss: movement lives under ArenaPlayerController
    // (Shared/Gameplay) and AdvancedCharacterControllerBase (Shared/Character).
    terms: ["wasd", "movement", "locomotion", "parkour", "character", "controller"],
    folders: ["Shared/Character", "Shared/Gameplay", "Shared/Bootstrap"],
  },
  {
    id: "combat",
    terms: ["damage", "hit", "health", "stagger", "poise", "combat"],
    folders: ["Shared/Combat", "Shared/Combat/Mechanics", "Shared/CombatBases"],
  },
  {
    id: "camera",
    terms: ["camera", "cinematic", "follow", "orbit", "shake", "fov"],
    folders: ["Shared/Cameras", "Shared/Rendering/Retro"],
  },
  {
    id: "rope",
    terms: ["grapple", "hook", "rope", "lasso"],
    folders: ["Shared/Rope", "Shared/Templates"],
  },
  {
    id: "ai",
    terms: ["ai", "behavior", "utility", "goap", "boid", "companion"],
    folders: ["Shared/AI", "Shared/AI/GOAP", "Shared/AI/Utility", "Shared/AI/Boids", "Shared/AI/Companion", "Shared/AI/Memory"],
  },
  {
    id: "input",
    terms: ["interact", "pickup", "door", "chest", "lever"],
    folders: ["Shared/Input"],
  },
  {
    id: "save",
    terms: ["save", "progression", "quest", "economy", "inventory"],
    folders: ["Shared/Save", "Shared/Progression", "Shared/Quests", "Shared/Items"],
  },
  {
    id: "animation",
    terms: ["animation", "motion", "retarget", "mocap", "locomotion"],
    folders: ["Shared/Animation", "Shared/MotionMatching", "Shared/MotionMatching/Locomotion"],
  },
  {
    id: "physics",
    terms: ["ragdoll", "destruct", "vehicle", "cloth", "physics"],
    folders: ["Shared/PhysicsExtra", "Shared/Physics", "Shared/Vehicle"],
  },
  {
    id: "ui",
    terms: ["hud", "healthbar", "crosshair", "settings", "ui"],
    folders: ["Shared/UI", "Shared/HudBases", "Shared/BlindAccessibility"],
  },
];

function normalizeTerm(term) {
  if (typeof term !== "string") return "";
  return term.trim().toLowerCase();
}

/**
 * Families that contain the exact normalized term (no substring/fuzzy).
 */
export function familiesForTerm(term) {
  const n = normalizeTerm(term);
  if (!n) return [];
  return SYNONYM_FAMILIES.filter((f) => f.terms.some((t) => normalizeTerm(t) === n));
}

/**
 * Folders relevant for a single term. Exact folder strings from the table.
 */
export function foldersForTerm(term) {
  const families = familiesForTerm(term);
  const set = new Set();
  for (const f of families) for (const folder of f.folders) set.add(folder);
  return [...set];
}

/**
 * Expanded query terms: original term plus every synonym in its families,
 * deduplicated, lowercased, bounded. This is what a "WASD → movement"
 * retry should search for.
 */
export function expandedTermsFor(term) {
  const n = normalizeTerm(term);
  if (!n) return [];
  const families = familiesForTerm(n);
  if (families.length === 0) return [n];
  const set = new Set([n]);
  for (const f of families) for (const t of f.terms) set.add(normalizeTerm(t));
  const expanded = [...set];
  return expanded.slice(0, MAX_EXPANDED_TERMS);
}

/**
 * Folders relevant for any term in a list (e.g. expanded query terms).
 */
export function foldersForTerms(terms) {
  if (!Array.isArray(terms)) return [];
  const set = new Set();
  for (const t of terms) {
    for (const folder of foldersForTerm(t)) set.add(folder);
  }
  return [...set];
}

/**
 * Auditable boundedness check — used by tests and by the gate's own
 * startup self-check. Returns { ok, errors }.
 */
export function auditSynonymFamilies() {
  const errors = [];
  if (SYNONYM_FAMILIES.length > MAX_FAMILIES) {
    errors.push(`families ${SYNONYM_FAMILIES.length} exceeds MAX_FAMILIES ${MAX_FAMILIES}`);
  }
  const ids = new Set();
  for (const f of SYNONYM_FAMILIES) {
    if (!f.id || typeof f.id !== "string") errors.push(`family missing id`);
    if (ids.has(f.id)) errors.push(`duplicate family id ${f.id}`);
    ids.add(f.id);
    if (!Array.isArray(f.terms) || f.terms.length === 0) errors.push(`${f.id}: terms empty`);
    if (f.terms.length > MAX_TERMS_PER_FAMILY) errors.push(`${f.id}: terms ${f.terms.length} exceeds ${MAX_TERMS_PER_FAMILY}`);
    if (!Array.isArray(f.folders) || f.folders.length === 0) errors.push(`${f.id}: folders empty`);
    if (f.folders.length > MAX_FOLDERS_PER_FAMILY) errors.push(`${f.id}: folders ${f.folders.length} exceeds ${MAX_FOLDERS_PER_FAMILY}`);
    for (const t of f.terms) if (typeof t !== "string" || t.trim().length === 0) errors.push(`${f.id}: empty term`);
    for (const folder of f.folders) {
      if (typeof folder !== "string" || folder.trim().length === 0) errors.push(`${f.id}: empty folder`);
      if (!folder.startsWith("Shared/")) errors.push(`${f.id}: folder "${folder}" must start with Shared/`);
    }
  }
  return { ok: errors.length === 0, errors };
}

// Self-audit at import time (fail-open: log, do not throw — gate must never
// brick tool calls due to its own data typo).
const _audit = auditSynonymFamilies();
if (!_audit.ok) {
  console.error("[behavior-synonyms] audit failed:", _audit.errors.join("; "));
}
