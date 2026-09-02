// Pure edit-window state for the flaxmcp-nav C# gate.
//
// The caller checks admission before the edit and records the edit only after
// the host reports success. Failed downstream gates therefore spend no proof.

export const MAX_EDITS_PER_NAV = 3;
export const NAV_GATE_PROBLEM = "__flaxNavGateProblem";

export function createNavState() {
  return { editsSinceNav: 0 };
}

export function resetNavState(state) {
  state.editsSinceNav = 0;
}

export function hasEditCredit(state) {
  return state.editsSinceNav < MAX_EDITS_PER_NAV;
}

export function recordSuccessfulEdit(state) {
  state.editsSinceNav = Math.min(MAX_EDITS_PER_NAV, state.editsSinceNav + 1);
}

export function setNavGateProblem(output, message) {
  if (!output || typeof output !== "object") return false;
  Object.defineProperty(output, NAV_GATE_PROBLEM, {
    configurable: true,
    enumerable: false,
    value: { message },
  });
  return true;
}

export function takeNavGateProblem(output) {
  if (!output || typeof output !== "object") return null;
  const problem = output[NAV_GATE_PROBLEM];
  if (!problem || typeof problem.message !== "string") return null;
  delete output[NAV_GATE_PROBLEM];
  return problem;
}
