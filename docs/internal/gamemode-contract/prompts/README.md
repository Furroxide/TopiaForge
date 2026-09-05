# Gamemode redesign execution prompts

These are bounded handoffs for the accepted eight-slice redesign. Read the
[canonical brief](../../GamemodeContractRedesign.md) and
[evidence ledger](../Status.md) before selecting a slice. The brief is normative;
this index supplies the common execution and verification rules. Historical
branches, PR descriptions, external briefs, and passing tests are evidence to
inspect, not an alternative specification.

## Sequence

| Slice | Prompt | Prerequisite |
| --- | --- | --- |
| 1 | [Review and planning documents](01-review-and-documents.md) | Confirm current `dev` and historical review sources |
| 2 | [Unused V6 contract and conformance](02-v6-contract-and-conformance.md) | Slice 1 merged into `dev` |
| 3 | [Pure resolution and transport models](03-resolution-and-transport.md) | Slice 2 merged into `dev` |
| 4 | [Runtime ownership and transitions](04-runtime-ownership-and-transitions.md) | Slice 3 merged into `dev` |
| 5 | [Binding and world adapters](05-binding-and-world-adapters.md) | Slice 4 merged into `dev` |
| 6 | [Runtime activation and manifest flip](06-runtime-activation-and-manifest-flip.md) | Slice 5 merged into `dev` |
| 7 | [Launcher, CLI, and overlay integration](07-launcher-cli-overlay.md) | Slice 6 merged into `dev` |
| 8 | [V5 retirement and acceptance](08-v5-retirement-and-acceptance.md) | Slice 7 merged into `dev` |

## Common execution rules

1. Read the repository's `AGENTS.md` and `CONTRIBUTING.md`, the canonical brief,
   this index, the selected prompt, and the current evidence ledger. Confirm
   the worktree, branch, tracked changes, remote, and prerequisite merge before
   editing. The approved working checkout is `C:\Users\vanst\Code\TopiaForge-gm`;
   the sibling `TopiaForge` checkout is shared and is not this implementation
   worktree. Preserve unrelated work in either checkout.
2. Fetch and compare with remote `dev` at the start, before every push, and
   after long builds. Create each next branch from `dev` only after its
   predecessor merges. Rebase instead of merge. Preserve superseded source
   commits; do not force-push or close a historical PR merely to tidy history.
3. Deliver one independently green slice per PR targeting `dev`. Inspect and
   reuse old work selectively; do not cherry-pick the full historical stack
   without checking its intermediate behavior. Do not broaden a slice silently:
   record any necessary re-cut in the brief and ledger before changing it.
4. Stage explicit owned paths, never `git add -A`. Use Conventional Commits and
   `Signed-off-by` on every commit. Inspect the actual branch/upstream, and push
   explicitly to the intended feature branch. Never use a bare push whose
   destination could be `dev`; do not assume an old upstream still exists.
5. Write a regression fixture/test for each confirmed defect before fixing it.
   New C# suites must run from the harness's no-argument full-run path, not only
   behind a flag. Keep non-generated Dart, including tests, at or below 500 lines.
6. Keep pure resolution and manifest code free of Unity dependencies; use Bloc
   for launcher application state and the TopiaForgeUi kit for in-game UI.
   Preserve package format, dependency ordering, inbox, logs, enablement,
   restart-required behavior, notices, and clean-room boundaries.
7. Do not reopen accepted choices: V6 replaces V5; contribution declarations
   live under `contributions`; `options`, `optionValues`, and
   `sessionExtensions` are excluded; declaration IDs have a separate 96-character
   ASCII grammar; `GamemodeHost` is retired at activation; the Worlds SDK
   assembly identity stays `0.1.0.0`; competing launches return Busy.

## Verification and handoff

Use the pinned tool versions declared by the current checkout. On the recorded
Windows development machine, invoke Flutter-bundled Dart explicitly at
`C:\Users\vanst\fvm\versions\3.44.6\bin\cache\dart-sdk\bin\dart.exe`, after verifying
that it matches the repository pin. Do not use the unrelated `dart` on PATH.
Run Flutter's Windows wrapper from PowerShell. Read current CI for required
setup rather than inventing environment variables or assuming local tools exist.

Run the relevant `AGENTS.md` checks for the slice, its acceptance checks, and
current required CI. For changes to C# or its baselines, rebuild Release before
running `bash tools/verify-csharp-release-surface.sh`: its seven harnesses use
`--no-build`, and API baselines are embedded resources. A stale binary is not
evidence for an edited baseline. Apply Dart formatting, then verify
`dart format --output=none --set-exit-if-changed` and `dart analyze --fatal-infos`
over changed packages, and audit tracked non-generated Dart line counts.

For document/content changes and before a push, run the repository audits:

```text
python3 .github/scripts/check_readme_counts.py
python3 .github/scripts/check_topiaforge_residue.py
python3 .github/scripts/check_trademark_notice.py
python3 .github/scripts/check_asset_licence_coverage.py
node website/scripts/check-markdown-links.mjs
```

Use an available Python 3 interpreter if its executable name differs. Derive
counts from the resulting tree, not old brief numbers. Documentation publication
means `npm run check` in `website` with the CI prerequisites, including pinned
.NET tools and managed references; a Markdown-link pass alone does not prove
publication. Do not stage generated build output unless the repository owns it.

Run `launcher_data` tests on Windows when that package changes. Reproduce a
failure against a clean checkout of the matching base revision before labeling
it local divergence. The previously reported multiplayer failures are not a
permanent exemption; record exact cases, commands, revisions, and CI results.

Update the ledger with changed behavior, source revision, tests and results,
remaining blockers, and the independent implemented/connected/automated-tested/
game-verified states. Obtain CI against the slice's actual `dev` base. Do not
claim game correctness from unit tests or mark later slices complete because
an interface compiles. Record unavailable native or visual checks as pending.
