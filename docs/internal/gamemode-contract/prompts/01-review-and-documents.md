# Slice 1: Review and planning documents

Implement only the planning and evidence slice. First read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
If creating these documents for the first time, use the accepted implementation
plan and repository evidence; do not present proposed behavior as existing code.

## Establish the baseline

- Inspect the current remote `dev`, the approved gamemode worktree, historical
  implementation branches, and PRs. Record full revisions and dated PR states.
  Prior statements about `f3de112` and PRs #102-105 are historical starting points
  to verify, not current-state guarantees.
- Preserve `docs/GamemodeArchitectureReport.md` as historical evidence. Trace
  GM-01 through GM-10 to present code, proposed fixes, and the acceptance evidence
  required to close each finding. Distinguish reproduced defects from source
  tracing and untested Unity behavior.
- Inventory claims in external briefs, internal plans, active guides, templates,
  website navigation, and PR descriptions. Correct premature completion claims
  in owned repository documents; record other required corrections by slice.

## Deliverables

- Create or replace `docs/internal/GamemodeContractRedesign.md` with the settled
  contract, resolution rules, lifecycle and ownership model, activation boundary,
  transport behavior, eight sequential slices, and measurable acceptance criteria.
- Create `docs/internal/gamemode-contract/Status.md` with revision-specific
  findings and separate implemented, connected, automated-tested, and
  game-verified states. A green test run cannot close an unconnected launch path.
- Supply one bounded execution prompt per slice and a shared index. Each prompt
  must name its prerequisite merge, deliverables, tests, exclusions, and handoff.
  Link to the canonical brief and ledger rather than duplicating stale snapshots.
- Replace both external ManifestV6 briefs with short supersession pointers to the
  canonical repository brief, preserving historical content in source history or
  a specifically identified archive first. Verify actual locations and request
  only any filesystem access that the environment requires; do not assume a
  sibling path is writable or invent a second brief filename.
- Amend the repository agent guide to reflect the accepted V6 and launch-API
  retirement decisions while preserving its other compatibility boundaries. State
  the exact assembly scope of additive SDK requirements so the Worlds redesign
  does not contradict the guide.

## Acceptance

- Every GM finding has an implementation slice, acceptance criterion, and honest
  current evidence status. Every later slice can be handed off independently
  without deciding the already settled design again.
- Existing source commits remain available. The canonical alias and first-party
  runtime behavior are unchanged in this documentation slice.
- All relative links resolve in the proposed tree. Future source files are named
  as code paths rather than broken Markdown links. Run the documentation and
  repository audits from the shared index; record full publication results or an
  exact pre-existing failure and its assigned corrective slice.
- Update the ledger and submit this slice alone against `dev`. Do not create the
  slice 2 branch until this PR merges. Do not mark the redesign complete.
