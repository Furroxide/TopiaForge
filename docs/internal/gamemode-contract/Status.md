# Gamemode contract redesign status

Updated: 2026-09-05. This is an evidence ledger, not a completion declaration.
The [canonical brief](../GamemodeContractRedesign.md) is normative. The
[architecture report](../../GamemodeArchitectureReport.md) preserves the original investigation.

## Revision and delivery boundaries

| Revision or artifact | Meaning | Integration status at this update |
| --- | --- | --- |
| `9811ff8f78697f1302760b57918e942613f2fc43` and the 2026-09-03 working tree | Historical architecture investigation; its uncommitted state is part of its evidence | Historical, not a description of today's checkout |
| `f3de112` | Reviewed tip of the original Manifest V6 stack | Source material only; PRs #102–105 were open and unmerged when reviewed |
| `768fb0f386b5c16ac35d124e12819d58b60816b7` (`origin/dev` when the replacement slice was cut) | Base of `docs/gamemode-redesign-plan` in the registered `TopiaForge-gm` worktree | First replacement slice contains documentation only |
| `f2ec48b14a86adc93c8079fd1216e91a2d6a2764` | Slice 1 squash merge of PR #106 into `dev` | Canonical documents integrated; no production or live acceptance claim |
| `feat/gamemode-v6-parity`, based on `f2ec48b` | Slice 2; imported source through `16181af`, followed by regression-driven repairs | In progress; canonical alias and production manifests/templates remain V5 |

On 2026-09-05, PRs #102–105 were converted to draft for the approved re-cut.
Their branch tips and existing review history were preserved. Both external
ManifestV6 briefs were replaced with supersession pointers; their original contents
remain in adjacent `.superseded-2026-09-05.md` files.

The original stack is retained for selective reuse. Its V6 alias flip, migrated declarations,
SDK additions, resolver, and V5 retirement are **not shipped**. A file existing on that stack
neither establishes that production calls it nor permits skipping its replacement slice's tests.
Refresh remote state and PR status before every subsequent handoff; the table above is a dated
snapshot, not a durable assertion about GitHub.

## Evidence from the review of `f3de112`

These are results recorded during the review that motivated the replacement plan. They do not
certify the current documentation branch or a future reconstruction of the source stack.

| Check | Recorded result | What the result establishes and does not establish |
| --- | --- | --- |
| Fixture inventory | 47 cases: 28 serialization, 19 resolution, 0 schema-channel cases | Existing coverage count only. The harness validates the fixture envelope, not each manifest payload against the V6 schema. |
| Focused Dart contract checks | 12 tests passed | Existing checks agree with their existing expectations; missing regressions and incorrect expectations can still pass. |
| C# investigation probes | Used existing Release outputs, without a fresh rebuild for that review | Probe observations only. Embedded API baselines and source changes were not certified by a new Release build. |
| Documentation publication preparation | Failed: 8 unpublished links to `ManifestV6.md` | The original stack's documentation is not publication-ready. A successful markdown or count audit does not override this failure. |
| Fixture, README count, residue, trademark, and asset-licence audits | Passed on the reviewed stack | Those respective repository invariants passed there; they do not certify runtime integration or publication. |
| Full relevant Release/Dart/Flutter matrix on the replacement branch | Not yet recorded | No new full-suite green claim. |
| Live game and native-timing acceptance | Not run | Scene readiness, placement, transition races, teardown under injection, and Free Play gameplay remain unverified. |

The focused Dart command was run from `packages/launcher_domain` at the reviewed revision
(the machine-specific home directory is expressed portably here):

```powershell
& (Join-Path $env:USERPROFILE 'fvm\versions\3.44.6\bin\cache\dart-sdk\bin\dart.exe') test test/gamemode_contract_conformance_test.dart test/manifest_schema_test.dart --reporter expanded
```

It ended with `+12: All tests passed`. The publication-preparation check was
`node website/scripts/prepare-docs.mjs --check` from the repository root. The review recorded
its eight unpublished `ManifestV6.md` links; it did not retain a complete per-link list in this
ledger. Rerun publication checks on the reconstructed slice instead of treating that count as a
current failure signature.

The previously reported seven local `launcher_data` multiplayer test failures are a historical
observation, not an exemption. For any recurrence, run the same command and environment against
a matching clean `dev` baseline; record both revisions and outputs. A different failure signature
or one that appears only after the change is a regression until explained. CI Windows evidence
and local evidence must remain separate.

## Slice 1 local verification

The documentation commit containing this ledger was checked on 2026-09-05:

| Command or check | Result |
| --- | --- |
| Website `node --test`, using bundled Node 24.19.0 after locked `npm ci` | 33 passed, 0 failed |
| `node website/scripts/check-markdown-links.mjs` | Passed across 124 Markdown files |
| `node website/scripts/prepare-docs.mjs --check` | Passed: 25 pages and 5 snippets from 3 compiled-template sources |
| `python .github/scripts/check_readme_counts.py` | Passed |
| `python .github/scripts/check_topiaforge_residue.py` | Passed |
| `python .github/scripts/check_trademark_notice.py` | Passed |
| `python .github/scripts/check_asset_licence_coverage.py` | Passed |
| `git diff --check` | Passed |

The initial website-test attempt used PATH's Node 23.8.0 without this worktree's
website dependencies and failed importing `yaml`. Locked dependency installation
and Node 24.19.0 resolved that setup failure; no repository dependency versions
changed. [PR #106](https://github.com/Furroxide/TopiaForge/pull/106) merged after
[CI run 33973852651](https://github.com/Furroxide/TopiaForge/actions/runs/33973852651)
passed on head `16c75babdd19a0c2c74536b4ea9bdac21cb49dbd`, with all five required
checks present and review threads resolved. Normal protected-branch merge was used.
These documentation checks do not close any production or live-game acceptance.

Additional baseline checks on the documentation branch (runtime sources identical to
`768fb0f`) passed: a fresh Release build with zero warnings/errors and the rebuilt
`verify-csharp-release-surface.sh` run across eleven SDK packages and all seven test
harnesses. The focused Windows multiplayer pack test reproduced the historical seven
failures under the inherited PATH (`powershell.exe` was not found by child commands).
The same unchanged sources passed all ten focused cases with a short explicit PATH
including Windows PowerShell; the complete data suite then passed 354 tests with four
expected skips. This is an environment diagnosis, not a source-test waiver. Subsequent
Windows tests use that explicit child-process PATH and record any new failure.

## Slice 2 local verification

Before reader repairs, the strengthened Dart harness validated each payload against the
pinned V6 schema and produced 29 reader failures (51 checks passed). These failures
reproduced invalid acceptance and diagnostic drift for raw scalar/null/conditional
inputs, collection uniqueness, and typed local references. Schema expectations passed.
Additional failing fixtures preceded direct-model validation and longest-owner
repairs. The final corpus has 149 cases: 18 intent operations, 125 manifest reader
cases, and six direct-model mutation cases. Every manifest payload is checked against
locked `json_schema` 5.2.2; successful cases require complete structured values and
serializer/reparse agreement. The harness also checks that every schema contribution
field is represented in normalization.

Before launch-path repairs, `declaration_id_launch_paths_test.dart` had six failures
and three passes; `world_authoring_declaration_id_test.dart` had two failures and two
passes. Legal 65/96-character IDs were dropped or rejected, and invalid menu world
references survived catalog filtering. After the fixes, the domain boundary plus
existing profile tests passed 15 cases, and authoring boundary plus existing world
authoring tests passed 21 cases. Package IDs retain the separate 64-character limit.
These tests run from each package directory using Flutter 3.44.6 bundled Dart.

| Final local command/check on the slice 2 working tree | Result |
| --- | --- |
| `dotnet build TopiaForge.slnx -c Release --nologo` | Passed, zero warnings/errors |
| `bash tools/verify-csharp-release-surface.sh` after that build | All eleven SDK packages and seven harnesses passed; 148 conformance cases at that run |
| Rebuilt manager harness with `--gamemode-contract` after the final capability oracle case | 149/149 cases passed |
| `dart test` from launcher_domain | 479 passed, including 267 focused conformance checks |
| `dart test` from launcher_data with the verified short Windows PATH | 358 passed, four expected Windows symlink skips |
| `dart test` from topiaforge_cli with the same PATH | 227 passed, four expected platform skips |
| `dart analyze --fatal-infos` from each of those three packages | Passed; two initial test-helper brace infos were corrected |
| `dart format --output=none --set-exit-if-changed` across their lib/test/bin trees | 332 files, zero changes |
| Fixture index audit and Python fixture self-tests | 149 indexed cases; 23 self-tests passed |
| Tracked plus new non-generated Dart line audit | No file over 500 lines |
| README counts, residue, trademark, asset-licence, Markdown links, content preparation | Passed |
| Full website publication check | Website tests, Astro and DocFX pass; domain/data Dartdoc pass. Launcher UI Dartdoc hits the identical Windows toolchain crash reproduced on clean `f2ec48b`; isolated SDK-source workaround under investigation |

Publication setup history: restoring repository-pinned DocFX 2.78.5 fixed the first
attempt. The second failed in Dartdoc 9.0.4 `_stripDocImports` with
`RangeError (end): 0..9089: 9202` after precaching 653180 Flutter elements.
An archive of unchanged `f2ec48b` domain/data/UI packages reproduced the exact crash
using the same Flutter 3.44.6 SDK. That SDK has CRLF Flutter source files; the existing
`tools/bootstrap-dev.ps1` documents this upstream Dartdoc issue. No SDK or source
versions were changed. Remote publication CI and an isolated source-copy check remain
separate evidence; this is not a permanent publication exemption.

The canonical schema alias and all first-party manifests/templates remain unchanged
at V5. Production launch activation is deliberately absent. Slice 2 CI and merge are
not yet recorded; runtime, Flutter application integration, and live game evidence
remain with their later slices.

## Prioritized repair ledger

Paths in this section identify source at **`f3de112`**, unless labeled historical. They are
revision-qualified text, not links that imply those files exist in the current `dev` tree.
P1 blocks safe integration; P2 blocks treating the published contract as complete.

| ID | Priority | Finding and source evidence | Required repair and closure evidence | Slice |
| --- | --- | --- | --- | --- |
| V6-01 | P1 | The conformance runner checks `fixture.schema.json`, permits per-runner expectations, and inventories only declared channel directories. `packages/launcher_domain/test/gamemode_contract_conformance_test.dart`; C# counterpart `GamemodeContractConformanceTests`. | Validate manifest payloads against the pinned V6 schema; distinguish structural and semantic expectations; recursively close the whole corpus; require equal codes and normalized values for equivalent operations. Prove orphan, misplaced, unknown-channel, and missing-expectation cases fail. | 2 |
| V6-02 | P1 | Structural readers lose raw type/presence information before semantic validation. `_contributionStructuralIssues`, `_contributionItems`, `ModManifestJson.ValidateContributionsObject`, and `NormalizeContributions`. | Add failing scalar, fractional integer, null, prohibited-empty-field, and conditional-branch fixtures before repairing both readers. Preserve absence separately from explicit values. | 2 |
| V6-03 | P1 | C# declaration/type validation uses `char.IsLetterOrDigit`; schema/Dart use narrower grammars. `ManifestContributionValidator` also validates normalized strings and collection values. | Share an ASCII identifier/type grammar, Unicode character-count semantics, length boundaries, collection bounds, and uniqueness through fixtures. Cover declaration IDs at 65/96/97 characters and supplementary Unicode text at limits. | 2, 3, 7 |
| V6-04 | P1 | Local world references consult all owned declaration IDs, and consent references do not establish a local gamemode declaration. `ManifestContributionValidator.ValidateWorldReference` / `ValidateReference`; Dart `manifest_contribution_references.dart` and `_validateWorldDeclaration`. | Resolve references against the required declaration kind and reject dangling consent. Test a gamemode used as `world.default`, a world used as `openTo`, and unresolved local IDs. | 2 |
| V6-05 | P1 | Ownership lookup is not uniform across target, disabled package, and discovered family paths. C# `LaunchResolver.ProfileIndex`; Dart `launch_resolution/launch_resolver.dart` and `world_and_transition.dart`. | Apply longest-prefix ownership before declaration lookup, including nested disabled packages and discovered families. Prove input order cannot change results and a longer owner never falls through to a shorter one. | 3 |
| V6-06 | P1 | Resolver requires target dependencies for open-policy player choices, exempts the default from open consent, and does not consistently default override permission to false. `LaunchResolver.ResolveWorld`, `AdmittedByPolicy`, `AdmitsChoice`; Dart world-policy helpers. | Require dependencies/versions for declared references, compatibility and consent for open player choices, consent for open defaults, and explicit override permission. Correct discovered-default fixtures and record concrete instance identity separately from its family. | 3 |
| V6-07 | P1 | Early resolver returns hide independently determinable blockers; `LaunchPlan.ResolvedPackages` retains mutable `ResolvedPackage.Manifest`. `src/TopiaForge.ModManager.Core/LaunchResolution.cs`, both resolvers. | Return deduplicated ordinal-sorted blockers; copy immutable package identities. Mutate source collections/manifests after resolution in tests and verify plan stability. Revalidate identities and resolve loaded declarations again before scene work. | 3, 5, 7 |
| V6-08 | P1 | V3/V4 and V5 migration use different preservation paths; legacy arrays can be filtered by `whereType<Map>`, and stub writes are direct. `apps/topiaforge_cli/bin/topiaforge_manifest_migration_commands.dart`. | Preserve untouched JSON values/presence, reject malformed entries with original index and ID, require author-only facts, share preservation/refusal rules, and write atomically. Test no-write failure paths and deliberately invalid incomplete stubs. | 8 |
| V6-09 | P1 | Declaration schemas and resolver types exist without an authoritative production launch consumer. Historical `GamemodeHost` startup, Worlds loading/session paths, launch repository, and manager routes remain the execution mechanism. | Connect binding, one session lifecycle, one transition executor, readiness, exact-profile resolution, and all launch surfaces before claiming the redesign runs. Use production integration fixtures and isolated-profile game acceptance. | 4–7 |
| V6-10 | P2 | Old authoring commands and templates can still imply metadata alone creates a gamemode; reference and publication wiring lag the new schema. Migration commands, scaffold paths, `docs/ManifestV6.md`, `website/scripts/docs/catalog.mjs`. | Reject obsolete authoring inputs before filesystem writes; migrate templates with runtime activation; publish a complete V6 reference and corrected compatibility/retirement guidance; pass documentation publication. | 1, 6, 8 |

## GM-01–GM-10 acceptance map

These identifiers retain the architecture report's original meanings. None is closed merely
because a proposed schema contains a related field.

| Finding | Required implementation | Automated acceptance | Live acceptance | Slices |
| --- | --- | --- | --- | --- |
| GM-01: ambiguous target and implementation ownership | Owned manifest launch targets, exactly one verified factory binding, one authoritative startup path | Duplicate/invalid binding failures; generated packages bind through production; no event subscriber creates a second controller | Cold launch of each first-party target reaches its declared world with one controller | 2, 3, 5, 6, 7 |
| GM-02: catalog/preflight versus effective profile | Resolve exact installed/enabled/pinned packages; scoped observations; enforce before process creation and again at runtime | Empty profile, pin changes, disabled provider, registry-only data, stale/removal observations, and digest mismatch block correctly | Switch profiles and disable/uninstall providers; unavailable targets remain actionable | 3, 5, 7 |
| GM-03: None/default/unavailable selection conflict | Explicit main-menu command; one selection operation; preserve unavailable legacy values with repair choices | Main-menu wins over remembered autoload; safe mode uses menu; direct startup alone can use remembered choice; ambiguous migration preserves input | Home, Setup, CLI, and manager select consistently; unavailable choices stay visibly unavailable | 3, 7 |
| GM-04: requested world differs from actual content | Provider returns actual scene and world instance; policy resolves permitted world/transition; no silent substitution | Target/world permutations, denied overrides, discovered-instance identity, content failure, and actual-scene reporting | Open Sandbox geometry/environment/kill-plane; two worlds sharing a mode load their selected content | 3, 5, 6, 7 |
| GM-05: premature or false launch success | Await scene/content/player/spawn and factory startup; success only at Running; correlated progress/outcomes | Delayed/failing/null startup, cancellation in every phase, reentrancy, stale completion, and missing acknowledgement | Cold launch and injected startup failures report truthful outcomes without premature gameplay | 4, 5, 6, 7 |
| GM-06: failed startup leaks session work | Create child scope before construction; rebind resource-producing service facades to it | Acquire subscriptions/UI/leases then throw in constructor/start; every acquired resource is released at failed-session boundary | Repeated failed launch/stop/restart and owner unload leave no gameplay callbacks or HUDs | 4, 5, 6 |
| GM-07: cleanup exception interrupts teardown | Attempt every cleanup action, aggregate errors, release ownership, emit exactly one terminal outcome | Controller/content/scope disposers throw independently and together; later cleanup and terminal notification still occur | Fault-injected stop/main-menu/provider unload permits a clean subsequent launch | 4, 5, 6 |
| GM-08: scene update restarts controller | Stable session identity and explicit `sceneChangePolicy`; notifications observe committed state | Keep-controller preserves one instance; end-session terminates explicitly; stale session operations cannot affect a successor | Native scene changes preserve or end the round according to policy | 4, 5, 6 |
| GM-09: competing native transitions | One executor for Worlds/core/local-world/restart/main-menu; Busy until native retirement | Concurrent admission, cancellation after dispatch, late arrival/quarantine, authority denial, and draining work | Race framework scene requests with launch/restart; verify native scene and input state | 4, 5, 6 |
| GM-10: declared authored spawn ignored | World instance returns resolved spawn; provider validates marker readiness; unsupported dead schema remains excluded | Missing/duplicate marker fails; correct marker resolves before StartAsync; templates use production binding | Authored-marker world starts at intended position; Open Sandbox and discovered providers report actual spawn | 2, 5, 6, 8 |

## Replacement slice state

Each slice targets `dev`, is independently green, and starts only after its prerequisite merges.
The old four-PR stack is source material; it does not satisfy these eight delivery boundaries.

| Slice | Deliverable | Implemented | Connected to production | Automated evidence | Live evidence |
| --- | --- | --- | --- | --- | --- |
| 1 | Review/brief/status/prompts and premature-claim corrections | Merged in PR #106 (`f2ec48b`) | Not applicable | Documentation checks and required CI passed at `16c75ba`; baseline Release/seven-harness checks passed | Not applicable |
| 2 | Unused V6 contract/readers/validators/conformance; alias stays V5 | Implemented on `feat/gamemode-v6-parity`, awaiting integration | Intentionally unused | 149 C# fixtures; 479 domain, 358 data, 227 CLI tests pass; seven Release harnesses pass; CI pending | Not required for pure contract |
| 3 | Pure resolution and immutable transport models | Original-stack candidate needs repairs | Intentionally no production switch | Corrected shared resolution/wire fixtures pending | Not required for pure models |
| 4 | Scoped ownership, lifecycle, shared transition foundations | Pending | Pending | Fault-injection and admission coverage pending | Native behavior pending |
| 5 | Verified factory bindings, providers/discovery, readiness adapters | Pending | Pending | Synthetic package and production-adapter coverage pending | World/readiness checks pending |
| 6 | Activate runtime and atomically flip manifests/templates | Original-stack migration is partial source material only | Pending | Rebuilt API baselines, generated package and consumer coverage pending | First-party gameplay pending |
| 7 | Launcher/CLI/overlay preflight, wire V4, observations, durable state | Pending | Pending | Cross-language wire/profile/process/progress integration pending | Cold launch and multi-surface selection pending |
| 8 | Retire V5, migration, publication, final acceptance | Original-stack migration/docs need repairs | Pending | Complete scoped matrix, audits, publication and CI pending | Full isolated-profile acceptance pending |

## Updating and closing this ledger

For each subsequent change, record the exact commit, branch/PR/base, test command and tool/runtime
version, observed result, and retained log or CI URL. Name both the producing and consuming
production paths before marking anything Connected. A fixture's asserted expectation is not
independent evidence that the policy is correct.

Rebuild test projects before checking embedded API baselines; a `--no-build` run only certifies
the assemblies it actually loads. Register every C# suite in the sequential no-argument full
runner as well as any focused flag. Run the scoped AGENTS checks, release-surface harnesses,
Dart format/analyze and line limits, repository audits, documentation publication, Flutter tests,
and Windows build required by the slice. A build or unit test cannot replace live acceptance.

For live records, include installed game/build, exact packages/profile, scenario, observed scene
and spawn, controller/resource cleanup, failure injection used, and outcome. Record Free Play
without Sandbox separately because it introduces new gameplay with no existing behavior to diff.
List any unavailable verification as pending with its concrete blocker; do not change it to pass.

Close a repair only with its regression result and integration evidence. Close a GM finding only
when the relevant automated **and** live acceptance cells are satisfied. Preserve failed outcomes
as history when appending later passes. Never prefill a future slice, CI run, merge, game test, or
publication as successful. Update this ledger in the same slice as the behavior it describes.
