# Gamemode contract redesign status

Updated: 2026-09-08. This is an evidence ledger, not a completion declaration.
The [canonical brief](../GamemodeContractRedesign.md) is normative. The
[architecture report](../../GamemodeArchitectureReport.md) preserves the original investigation.

## Revision and delivery boundaries

| Revision or artifact | Meaning | Integration status at this update |
| --- | --- | --- |
| `9811ff8f78697f1302760b57918e942613f2fc43` and the 2026-09-03 working tree | Historical architecture investigation; its uncommitted state is part of its evidence | Historical, not a description of today's checkout |
| `f3de112` | Reviewed tip of the original Manifest V6 stack | Source material only; PRs #102–105 were open and unmerged when reviewed |
| `768fb0f386b5c16ac35d124e12819d58b60816b7` (`origin/dev` when the replacement slice was cut) | Base of `docs/gamemode-redesign-plan` in the registered `TopiaForge-gm` worktree | First replacement slice contains documentation only |
| `f2ec48b14a86adc93c8079fd1216e91a2d6a2764` | Slice 1 squash merge of PR #106 into `dev` | Canonical documents integrated; no production or live acceptance claim |
| `c61d5fcf821ef726c2aea3ba35c092e314e1f6c4` | Slice 2 squash merge of PR #107, reviewed head `055a7d4` | Unused V6 contract integrated; alias and production manifests/templates remain V5 |
| `41f14d078dca1830749f70cac9f72083c4fedd8c` | Slice 3 merged through PR #108; corrected pure resolver, immutable plans and inactive transport models | Full CI passed at reviewed head `63540e2`; no production caller or wire activation |
| `9dc5613bc0cc8f2fa9b3c50b02357116f2540514` | Slice 4 squash merge of PR #109, reviewed head `4b12a1edcc56c033f1616931f40deaba44ee35c6` | Full CI and CodeQL passed; merged normally on 6 September with review threads resolved |
| `b7390fb6a8376c58c77d18f20e287bacbd427850` | Slice 5 merge of PR #110, final reviewed head `7907398cc9d21dc18d8b9481690dd434f7f30595` | Final exact-head CI and CodeQL passed; merged normally at 2026-09-06T02:07:23Z |
| `bd7be8be386c51713fa1099488e1e4d36ba130a5` | Slice 6 merge of PR #111, final reviewed head `584349c123c72075b8922093a9486ae0491d041d` | Exact-head CI, full publication and CodeQL passed; normal merge at 2026-09-06T04:46:28Z |
| `da47dc7f89462c4473bac54db7e1c98acecda52d` | Slice 7 merge of PR #112, reviewed head `7bc19231bf6ea12bb708ef318774d78fffee72cd` | Exact-head CI 34167871674 and CodeQL 34167870357 passed; normal merge at 2026-09-07T22:58:39Z |
| `31ff4d49bd30c593e484a01407e47ca7871f7ca7` | Slice 7a normal merge of PR #114, reviewed head `80fa8f9fb6e8b1a7cc4af0d8c6c00d31820e1437` | CI 34185465503 and CodeQL 34185463190 passed; all four review threads resolved; merged at 2026-09-08T04:10:41Z |
| `1fbd32d2524a13e2dad0d5f705da8301dec952e6` | Slice 8a normal merge of PR #115, final reviewed head `0c4a3061d286db17c4d6f20ca5b07489a42eeafe` | Full CI 34193577478 and CodeQL 34193574648 passed; all review threads resolved; merged at 2026-09-08T06:21:07Z |
| `0182e19f166383685210151f07948e9909977b03` | Slice 8b normal merge of PR #116, final reviewed head `eda671d40b51cddff098f34437510be552c6e46e` | Full CI 34199629221 and CodeQL 34199625355 passed; both review threads resolved; merged at 2026-09-08T07:42:38Z |
| `5acead7c855e899f0331b042a6794f424f48a467` | Documentation checkpoint normal merge of PR #117, final reviewed head `c2b410df7f6f11ea35525e25b265612631f11f26` | Full CI 34201562208 and CodeQL 34201557620 passed; review resolved; merged at 2026-09-08T08:03:20Z |
| `c0be924a23c6739ce5e1daa114e729261e8bd13d` | PR #118 integration, final reviewed head `54c9722ca3a6fc76410189c7801b9b50dc5945f5` | Full CI 34205824535 and CodeQL 34205822728 passed; review returned no findings; merged at 2026-09-08T08:51:03Z |


The historical rows describe each slice at its own merge. Current state:

| Area | Implemented | Connected | Automated-tested | Game-verified |
| --- | --- | --- | --- | --- |
| V6 contract, resolver, runtime lifecycle and binding | Integrated through slices 2-6 | Runtime activation integrated | Exact-head CI for each slice passed | Pending |
| Target selection, V4 wire and correlated outcomes | Integrated through slice 7 | Home, Setup, CLI and manager share resolution | Slice 7 exact-head CI and CodeQL passed | Pending |
| Candidate qualification and publication guards | Integrated through slice 7a | CLI, administrator and publication paths connected | Final reviewed-head CI and CodeQL passed at `80fa8f9`; 441 local CLI tests passed with four platform skips | Pending; synthetic acceptance is not game evidence |
| Unsigned RC package construction | Integrated after explicit authorization on 8 September | Builder and Windows orchestration honor recorded policy | Final PR #114 hosted checks passed; local regression details below | Pending |
| V5 retirement, migration and obsolete models | Integrated through slice 8a | Readers and CLI atomic writer connected; old models removed | Full final-head CI, including Windows data and publication, passed at `0c4a306` | Pending |
| Authored-world marker validation | Integrated through slice 8a | Editor validation and owned prefab instantiation connected | Full CI and CodeQL passed; fifteen filesystem checks and fourteen supplementary actual EditMode cases on 6000.0.31f1 pass | Pinned 6000.0.23f1 cases and actual spawn pending |
| Acceptance isolation | Integrated through slice 8b | Runtime, CLI and release admission connected with request-bound original receipts | Full final-head Windows/Linux CI, publication and CodeQL pass at `eda671d` | Pending isolated host and actual game evidence |

No release tag, release dispatch or publication has occurred. The checkout is
not release-ready. Source slices through 8b are integrated, but no subsequent
release branch or candidate can treat pending acceptance as complete.

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
| Full website publication check | Complete Linux CI publication passed at `055a7d4`. Local Windows UI Dartdoc hits a baseline toolchain crash; isolated LF copies remove that crash but expose 36 baseline annotation-link warnings |

Publication setup history: restoring repository-pinned DocFX 2.78.5 fixed the first
attempt. The second failed in Dartdoc 9.0.4 `_stripDocImports` with
`RangeError (end): 0..9089: 9202` after precaching 653180 Flutter elements.
An archive of unchanged `f2ec48b` domain/data/UI packages reproduced the exact crash
using the same Flutter 3.44.6 SDK. That SDK has CRLF Flutter source files; the existing
`tools/bootstrap-dev.ps1` documents this upstream Dartdoc issue. No SDK or source
versions were changed. An isolated LF source copy removed the crash but produced 36 inherited meta
annotation-link warnings; a supported staged linkTo override did not fix those
warnings. No global SDK or cache sources were changed. Retained local logs are
under the temporary reproduction directory `tf-dartdoc-baseline-spmb62k8`. This
local limitation is separate from passing Linux publication CI and is not a
permanent publication exemption.

The canonical schema alias and all first-party manifests/templates remain unchanged
at V5. Production launch activation is deliberately absent. [PR #107](https://github.com/Furroxide/TopiaForge/pull/107) merged at `c61d5fc` after
[CI run 33976277744](https://github.com/Furroxide/TopiaForge/actions/runs/33976277744)
passed on `055a7d4f537d48f49862ed60becf44731aa3e83a`. All five required checks,
Windows data tests, Flutter checks, seven template lifecycles and full Linux
publication passed. Updating the final PR description reran the policy check;
the merge waited for that check to pass and used normal branch protection.
Runtime activation and live game evidence remain with later slices.

## Slice 3 local verification

The replacement branch starts at merged `c61d5fc`. It imports only the pure
resolver and resolution fixtures from preserved `d314599`, leaving the alias,
first-party manifests, templates, runtime callers and wire V3 unchanged.

The pre-fix resolver failed cases for absent override permission, consent on open
policy defaults, invented dependencies on open player choices, concrete discovered
instance identity, combined blockers and copied plan identities. A preserved
isolated Dart copy of `d314599` additionally reproduced incorrect longer-owner
fallback for targets, disabled mode/world diagnostics and consent dependency ranges.
These probes ran independently of the repaired production sources.

Review of the replacement exposed further defects before integration: disabled
historical versions competing with an enabled selection, family availability not
propagating to its instances, and NoAvailableTarget being inferred despite an
independent compatibility failure. New cases reproduced those defects before the
corresponding fixes. Resolution fixtures now separately state schema and semantic
validity for every input manifest; an intentionally invalid robustness input cannot
silently stand in for a valid operational case.

Immutable transport descriptors retain the original request independently of the
resolved tuple. Inactive wire V4, progress, separate launch/session outcomes and
versioned observations have strict shared string-codec fixtures. Runtime binding
proof is separate from cached observations and must match the loaded profile.
Exact package identities and literal cross-language digest vectors are checked in
addition to re-resolution. The corpus contains 353 shared cases: 156 contract/legacy-wire,
67 resolution and 130 new transport cases. Both runners execute exact results and
input-order permutations; direct model suites additionally check copied snapshots,
constructor invariants, 4096/4097 collection limits and fresh binding revalidation.
These checks do not certify a production launch or live game behavior.

A final boundary review reproduced four Dart transport failures: empty discovery
suffixes were accepted while C# rejected them. Two valid 94-character-family /
96-character-instance controls passed. The manifest contract also allowed families
of 95/96 characters, which cannot represent any nonempty-suffix instance within
96. Six schema/reader/direct-model checks failed before the conditional max94
repair; static world95/96 and mode/target96 controls remained valid. All twenty
added checks pass after the fixes. The normative brief now states this reserve.

Local checks on the slice 3 boundary follow-up to `84a7f6d`, 5 September 2026:

| Command or check | Result |
| --- | --- |
| Fresh `dotnet build TopiaForge.slnx -c Release` | Passed, zero warnings/errors |
| Rebuilt `bash tools/verify-csharp-release-surface.sh` | All eleven SDK packages and seven harnesses passed, including 353 C# conformance cases and direct model tests |
| `dotnet format TopiaForge.slnx --verify-no-changes --no-restore` | Passed after formatting owned files and two unchanged local line-ending cases |
| Full launcher_domain `dart test` | 791 passed, including 579 conformance/model checks |
| Full launcher_data / topiaforge_cli `dart test`, verified short Windows PATH | 358 / 227 passed, four expected platform skips in each |
| Fatal-info analysis in domain/data/CLI | Passed; final full-domain analysis found one missing brace pair, corrected before handoff |
| Dart format verification across those lib/test/bin trees | Original three-package check: 348 files; boundary follow-up: 97 domain files, zero changes |
| Fixture closure and Python self-tests | 353 cases; 31 self-tests passed |
| Tracked plus new non-generated Dart line audit | 410 files; none over 500 lines |
| README count, residue, trademark, asset-licence audits | Passed |
| Markdown links and publication preparation | 124 files; 25 pages and five snippets from three compiled-template sources passed |
| Full publication and required CI | Passed at `84a7f6d` in run `33981203036`; boundary follow-up CI pending |

The first seven-harness attempt used the build preceding the final added consent
alternative regression and failed that case as expected. After the helper repair,
a fresh Release build and complete seven-harness rerun passed. Whole-solution
Windows formatting also flagged two files whose normalized content was unchanged
from `c61d5fc`; local line-ending normalization made the check pass without a
repository behavior change. Retained local build/format/harness logs use the
`tf-slice3-*` prefix in the temporary directory; package test logs are ignored under
`.dart_tool/slice3-*.log`. Exact committed CI evidence will be appended after review.

## Slice 3 integration and slice 4 start

PR #108 merged under normal protection on 5 September 2026 at
`41f14d078dca1830749f70cac9f72083c4fedd8c`. Full CI run `33982437483` passed at
reviewed head `63540e280cb5a9e15b5bdf8ece28b83e367c4ff3`, including Windows data,
Flutter, all seven templates and full documentation publication. All five required
checks passed, and the review thread was resolved before merge. Slice 4 was created
from refreshed `dev` only after that merge; the historical draft stack is preserved.

## Slice 4 verification in progress

The first slice 4 runtime regression used the unmodified runtime binary and a newly
built synthetic package to observe unload ordering. It expected
`load, stopping, unload:stopping, cleanup`, but recorded
`load, unload:active, stopping, cleanup`. Cancellation therefore occurred after the
extension unload callback. The replacement shutdown sequence begins cancellation,
awaits active session/native drain, then invokes package callbacks and disposers.
The fresh runtime build also reproduced this ordering in partial-load failure.
A separate regression showed that a throwing diagnostic sink aborted the remaining
independent package load. Both fixes pass the rebuilt runtime integration suite. Multi-package tests use
a second synthetic assembly: reusing one assembly under two package IDs is correctly
rejected by production ownership checks and was an invalid initial test setup.

Four RobotKit cancellation regressions failed against the real service source:
a cancelled scope could replace or clear a newer same-package objective, publish a
target, and receive delivery events. All four pass after early mutation guards and
subscription liveness checks. These isolated tests do not establish Unity behavior.

Controlled lifecycle tests reproduced a reentrant launch queued during Stopping
and later admitted at Idle, a stop requested from the Running observer being lost,
an oversized author exception stranding cleanup through transport validation, and
cancelled startup incorrectly producing a successful terminal session outcome.
Each fix has a failing regression followed by a passing focused run. The launch
outcome may report cancellation early; the terminal session outcome waits for
ignored callbacks and native work to drain.

Further regression-first fixes cover global shutdown admission, cancellation of
in-flight menu work, throwing cancellation callbacks, delayed scene notifications
from a previous session, and re-reading mutable provider readiness. Shutdown waits
for the complete admitted operation, including a replacement's temporary Idle
phase. Child asset scopes can spawn parent package prefabs without taking ownership
of the parent's handles. Asset initialization failures reclaim a newly allocated
object; tracking rejection transfers cleanup ownership without double disposal.

| Final local check on the slice 4 working tree | Result |
| --- | --- |
| Fresh `dotnet build TopiaForge.slnx -c Release --nologo` | Passed, zero warnings/errors |
| Reviewed SDK baseline regeneration, then rebuilt embedded resources | Only Worlds changed: 74 additive baseline lines; assembly identity remains `0.1.0.0` |
| `bash tools/verify-csharp-release-surface.sh` after rebuild | Eleven SDK packages and all seven no-argument harnesses passed, including all new suites and 353 shared fixtures |
| Rebuilt ModRuntime synthetic-assembly integration suite | Passed: cancellation before callbacks, partial-load failure, ignored work drain, throwing cleanup/logging and normalized scene observation |
| Flutter 3.44.6 bundled `dart test` from domain/data/CLI | 791 domain, 358 data and 227 CLI passed; four expected platform skips each in data/CLI |
| `dart analyze --fatal-infos` and formatter from those three packages | Clean; 348 files, zero formatting changes |
| Fixture index and closure self-tests | 353 cases and 31 self-tests passed |
| Non-generated Dart line audit | 410 files; none over 500 lines |
| README count, residue, trademark, asset-licence audits | Passed |
| Website tests, Markdown links and content preparation | 33 tests passed; 124 Markdown files, 25 pages and 5 snippets passed |

The first full C# harness run found an existing source convention requiring a
semicolon directly after `OnHostDisposed(this)`. The new attempt-all wrapper
retains that call inside a lambda; the convention now checks the invocation without
requiring that incidental punctuation. The complete rebuilt gate subsequently passed.
Local logs are retained in the temporary directory under `tf-slice4-*` names.
`dotnet format TopiaForge.slnx --no-restore --verify-no-changes` passed (workspace
load warnings only). `node scripts/build-reference.mjs` in website built the C#
reference with zero warnings/errors: 388 managed-reference inputs, 392 models and
391 HTML files. Full website publication remains an exact-head Linux CI gate;
the previously matched Windows Dartdoc baseline limitation is unchanged. First-head
[CI run 33986323680](https://github.com/Furroxide/TopiaForge/actions/runs/33986323680)
passed at `6caceb9f7163b1b5532b645c5e2d25af7226b740`, including all five required
checks, Flutter, Windows data, seven templates and full Linux publication. This
result does not certify the subsequent review corrections.
Production V6 binding/activation and every game/visual/native-timing acceptance
remain pending; the new orchestrator is exercised through controlled foundation
tests, while V5 production scene requests already use the shared executor.

## Slice 4 independent review corrections

Review of `6caceb9` reproduced additional failures before their fixes:

- Matching scene arrival could release native ownership before the managed game
  loader completed; a later loader fault then disappeared. Both completion signals
  are now required. Structured cancellation/authority/busy refusals no longer turn
  into fallback scene loads, and borrowed admission checks current authority.
- An undefined SDK error enum could interrupt wire-outcome construction and strand
  the session in Stopping. Author results are normalized before command completion
  and notification; positive and negative unknown codes have regression cases.
- Checkpoint subscriptions outlived cancellation. Callback delivery and subscription
  creation now check owner liveness.
- A disposer could submit another resource after the scope was marked disposed,
  escaping the final drain and its aggregated failure. Scope cleanup now seals
  ownership only after the rejected-resource and queued-work drain is empty.
- Runtime shutdown without a session hook disposed V5 package services while native
  work remained active. Missing, throwing, faulted and failed hooks all pass through
  the native drain; host attachment remains until package teardown completes.
- Unity does not await an `async void OnDestroy`. The plugin now starts shutdown
  synchronously and gives the process host an observed completion task. Controlled
  tests verify host-thread, exactly-once final cleanup after success or failure,
  including a throwing diagnostic sink. Process-exit timing is still unverified.

Independent cross-review then reproduced pending-constructor scope cleanup and
checkpoint diagnostic isolation failures, plus eleven native timeout/admission
failures. Async-only scope creation now awaits failed initialization cleanup and
returns a successfully created scope before the caller handles cancellation. Native
admission rechecks revocation, competing work, token/lifetime cancellation and the
session gate after provider-backed authority callbacks. A loader timeout completes
the caller while native ownership remains quarantined until both signals drain.

The scope control that disposed its lifetime midway through initialization initially
expected success incorrectly: later facade initialization correctly threw
ObjectDisposedException. It now explicitly checks failed initialization, retained
error evidence and cleanup. A separate completed-creation/cancel-before-consumption
control verifies that successful scope ownership is preserved.

Final local verification of the review follow-up to `6caceb9` passed: fresh full
Release with zero warnings/errors; rebuilt eleven-SDK-package and all seven
no-argument harnesses; whole-solution format verification; bundled-Dart fatal-info
analysis and format verification (348 files, zero changes); 353-fixture closure and
31 self-tests; 410 non-generated Dart files at or below 500 lines; README, residue,
trademark and asset-licence audits; 33 website tests, 124 Markdown-link checks and
25-page/five-snippet content preparation. The unchanged Dart test results above
remain scoped evidence; C# tests were rerun after the final corrections. Logs use
`tf-slice4-followup-*` in the temporary directory. The committed follow-up
`930d0a2a093f706e14152b035b6eb975051aeaf2` passed full exact-head CI in run
`33988344239`, including publication, Flutter and Windows data tests. No game or
native-timing result is implied. At that head, PR #109 remained unmerged: the repository
code-scanning ruleset independently blocked it on test-path alerts 428, 429 and 430.
Passing the five named required checks alone does not satisfy every merge rule.

The CodeQL annotations concern test fixture paths:
`Program.cs` creates a fresh GUID-named directory under `Path.GetTempPath`, and
`Program.SessionSceneTests.cs` reads trace paths created by the private fixture
helper under that test root with constant package names. These paths do not come
from a package manifest, wire request, or external user input. This assessment is
limited to those test sites; no global security rule or production path check was
relaxed. Review also corrected accidental mojibake in owned comments and this ledger;
UTF-8 decoding and a targeted encoding audit pass for the follow-up source files.

On 6 September, the test-root follow-up replaces manual temporary path selection
and directory creation with the framework atomic temporary-directory API. This
keeps per-run ownership and final cleanup while removing the select/create race.
No CodeQL alert has been dismissed or suppressed and no repository rule changed.
An earlier combined dismissal/merge request was rejected by automatic approval
review before execution; the source repair then received its own rebuilt harness and
exact-head CodeQL results before merge.

The atomic-root follow-up passes fresh Release with zero warnings/errors, all
eleven SDK package checks and seven no-argument harnesses, focused lifecycle and
Runtime tests, C# format verification, bundled-Dart fatal-info analysis and format
verification (348 files unchanged), line limits and repository/Markdown audits.
Local publication passed website tests, Astro, C# reference and domain/data Dart
reference generation, then stopped in bundled dartdoc 9.0.4 while documenting
`launcher_ui`: `_stripDocImports` raised `RangeError` (end 9202, length 9089).
This is an observed local tool failure, not a publication pass or a permanent
exemption; exact-head Linux publication remains required. The Flutter SDK was
not modified. Logs are `tf-slice4-security-*` in the temporary directory.

## Slice 4 merged; slice 5 underway

PR #109 merged normally on 2026-09-06 at 00:04:01 UTC as
`9dc5613bc0cc8f2fa9b3c50b02357116f2540514`. Final head
`4b12a1edcc56c033f1616931f40deaba44ee35c6` passed full CI run `33999792058`
(including Linux documentation publication) and CodeQL run `33999790502`.
All review threads were resolved and merge eligibility was clean. GitHub reports
alerts 428, 429 and 430 as fixed by source changes. No dismissal, suppression,
admin merge or repository-rule change was used.

Slice 5 was cut from that merge only afterward. Its working tree introduces
verified package binding and internal native-world services while keeping the V5
alias, manifests and templates in place. Focused regressions have demonstrated
readiness waits, partial bundle cleanup, late cancellation, foreign content
rejection and cleanup aggregation. These are working-tree observations; full
Release, seven-harness and exact-head CI evidence is not yet established for slice 5.
No declaration activation or game acceptance is implied.

The local Dartdoc failure is now independently isolated: Dartdoc 9.0.4 passes a
pure-Dart LF comment containing `@docImport`, but its byte-equivalent CRLF input
fails in `_stripDocImports`. A tiny LF Flutter project reproduces the exact
9202/9089 failure through the unchanged SDK's `animation.dart` comments. This
matches [upstream issue 4180](https://github.com/dart-lang/dartdoc/issues/4180).
Reproduction files and logs are retained under the ignored
`.dart_tool/dartdoc-repro-20260906/`; no SDK/cache modification or tool upgrade was
used. This local limit does not replace exact-head publication CI, which passed
for the merged foundation. The Windows release builder does not run this website
publication command.

## Slice 5 regression evidence in progress

Evidence below describes the uncommitted working tree on 2026-09-06, based on
`9dc5613bc0cc8f2fa9b3c50b02357116f2540514`. The slice is unmerged and remains in
progress. Its production path starts with an explicit immutable effective-profile
selection before `ModRuntime.Load`; unconfigured V5 callers retain their existing
entry path. Synthetic packages load from real receipt-verified DLLs in separate
child processes, exercising CLR location collisions and forwarding without manual
factory registrations. First-party declarations and templates remain V5; production
launch-surface activation is still assigned to slices 6 and 7.

The provider-allocation review exposed a larger required repair: cancelling a
public asset or core-scene task did not establish that the engine had completed
its work. Synchronous failed-entry cleanup could also drop the only context that
owned that pending work. Slice 5 therefore expanded beyond the initial provider
helpers into native asset/core-scene ownership, child/owner lifetime drain, session
cleanup and retained failed-entry cleanup. These changes are necessary for the
provider ownership contract to hold through constructor/start failures and unload.
They reuse the existing scene executor and compose its admission gates; they do
not activate a second launch protocol. This scope expansion and its additional
regressions must be included in the slice's review and final verification.

Focused regression-first corrections include:

| Boundary | Demonstrated defect and repaired behavior | Evidence state |
| --- | --- | --- |
| Verified entry assembly | An omitted optional entry hash allowed receipt verification before a file-sharing lease; mutation in that gap was accepted. Verify the inventory under the lease before loading the entry or declared implementation. | `entry-race` RED then GREEN; exact-head CI pending |
| Longest owner | A shorter package's valid declaration could publish binding evidence under a longer selected owner's namespace. Reuse the resolver ownership algorithm for bindings, failures and discovery. | Isolated `ownership` case passes; failed longer owner stays authoritative |
| Discovery lifetime | Package unload could run while a discovery callback retained its child scope. Register work before construction, revoke publication before shutdown, and await callback plus scope cleanup before package teardown. | `discovery-drain`, timeout and cancel-with-throwing-cleanup RED then GREEN; final retained-barrier regressions also GREEN |
| Discovery cancellation and cleanup | Worker cancellation invoked package callbacks off the host thread, and a successful work barrier hid disposer faults from shutdown. Dispatch caller/deadline cancellation on the host, retain callback/cleanup failures, and distinguish actual cleanup faults from handled family failures. | `discover-worker-cancel`, `discover-worker-cancel-callback` and strengthened `discover-cancel-cleanup` RED then GREEN |
| Failed discovery scope construction | Child construction could clean up before returning a context, leaving discovery without a scope reference from which to report cleanup failure. Carry the actual cleanup failure through the construction exception into the retained discovery barrier; ordinary construction failure remains availability evidence. | `discover-scope-construction` and `discover-scope-construction-cleanup` GREEN after the cleanup-propagation RED |
| World readiness | Scene arrival alone, missing/duplicate markers, foreign roots and unavailable players could not establish readiness. Capture actual scene identity and validate content/player/spawn before return. | Focused readiness suite GREEN; native/game evidence pending |
| Additive admission | Additive preparation skipped Busy and multiplayer authority checks and released ownership before readiness. Acquire through the same executor and retain admission through readiness or cleanup. | `TestAdditiveAdmission` RED then GREEN |
| Partial allocation | Owner cancellation during native preparation allocation and throwing native drain could skip or misclassify cleanup. Retain ownership immediately and attempt every disposer. | Allocation/drain fault cases RED then GREEN |
| Late native failure | An early cancelled caller result hid later managed/native/adapter faults. Keep immutable terminal native evidence separate from the caller acknowledgement and carry late faults into cleanup. | Cancellation, combined faults, provisional arrival and adapter-throw cases RED then GREEN |
| Generated geometry | The spawn platform extended above the exact resolved spawn. Its surface now meets that spawn without adding an arbitrary player offset. | Pure geometry RED then GREEN; collider clearance pending live acceptance |
| World owner stop | A late preparation returned after owner disposal was reported External. Preserve Cancelled while reclaiming the late owned result. | Provider loader RED then GREEN |
| Built-in ownership | Early disposal/start failure leaked tracking leases, owner cancellation missed pending native work, and stop during registration could leak the returned lease. Retain/release the lease through reentrant disposal and keep owner/caller cancellation linked for the actual work. | Ten built-in provider tests GREEN after focused ownership/cancellation REDs |
| Native asset ownership | Public cancellation could release a lifetime or backing bundle before a bundle/prefab request and its late cleanup completed. Enrol work before native allocation, retain backing resources, and distinguish a delivered expected failure from a later failure hidden by cancellation. | Thirteen fake-engine asset cases GREEN, including rejected handoff versus throwing late cleanup |
| Core-scene ownership | Core scene calls dispatched without lifetime enrollment, allowing a caller cancellation to hide late native failure from package/session cleanup. Enrol before the existing executor dispatch and observe native drain and terminal native evidence. | Five core-scene cases GREEN after missing-enrollment RED; existing scene-arrival behavior retained |
| Child/session drain | Synchronous disposal or failed scope construction could release parent ownership while native work continued. Close admission before cancellation, await native work before disposing controllers/worlds/scopes, retain Busy, and attempt every cleanup action. | Native lifetime/session/construction REDs then lifecycle suite GREEN; normal and throwing cleanup covered |
| Terminal diagnostics | Flattening a wrapped failure to its base message discarded independently recorded late-native cleanup failures. Preserve independent nested messages in the bounded terminal outcome and retain full diagnostic exceptions. | Combined late-native and ordinary cleanup regression GREEN; exactly one terminal outcome asserted |
| Failed entry cleanup | An `OnLoad` failure could call `OnUnload` and drop its context while assets or scene work remained pending. Retain a cleanup transaction before cancellation callbacks, drain native work before disposal, and await the transaction during shutdown. | `native-failed-owner` and `native-failed-scene` RED then GREEN; `native-owner-drain` GREEN |
| Composed cleanup admission | Session admission could overwrite the independent failed-package cleanup gate. Keep runtime cleanup blocking direct and lifecycle scene reservations and launch admission until the retained work retires. | `native-gate` RED then GREEN |

Focused evidence is deliberately separate from final whole-slice certification:

| Invocation or scope | Observed result and retained evidence |
| --- | --- |
| Manager harness `--world-providers` | Discovery identity, resource scope, player placement, generated geometry and provider loader suites pass. Logs: `.dart_tool/slice5-worlds-red.log`, `slice5-worlds-owner-red.log`, `slice5-worlds-green.log`. |
| Built-in provider ownership/cancellation | Ten tests pass. Logs: `.dart_tool/slice5-builtin-ownership-red.log`, `slice5-builtin-cancellation-red.log`, `slice5-builtin-ownership-green.log`. |
| `dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release -- --session-lifecycle` | Session lifecycle/results/orchestrator, built-in providers, readiness, asset drain and native-work lifecycle suites pass. The native-work REDs and final `slice5-native-work-lifecycle-green.log` are retained under `.dart_tool/`. Session-bound `StopAsync` acknowledges acceptance; the owner-stop test waits for complete cleanup. |
| Native asset/core-scene adapter runner | Thirteen asset invocations and five core-scene cases pass. Temporary-directory logs include `tf-slice5-asset-native-red.log`, `tf-slice5-asset-owner-handoff-red.log`, `tf-slice5-core-scene-native-red.log` and `tf-slice5-asset-and-scene-native-green.log`. |
| `dotnet run --project tests/TopiaForge.ModRuntime.Tests/TopiaForge.ModRuntime.Tests.csproj -c Release` | The final no-argument runtime harness passes, including all 40 isolated binding/runtime integration cases: the previous 36 plus two worker-cancellation and two scope-construction cases. This is the isolated-case count, not the total assertions or all harness tests. Temporary-directory log: `tf-slice5-discovery-final-runtime.log`. |
| Production project Release builds | Manager and Worlds each compiled with zero warnings/errors during focused work. Logs include temporary `tf-slice5-native-final-manager-build.log` and `.dart_tool/slice5-worlds-unity-build.log`. The final complete solution rebuild below also passed after every correction. |

Final discovery review repaired the lost shutdown-cleanup evidence and off-host
cancellation defects, then found and repaired the same cleanup-propagation gap
when child scope construction failed before returning a context. Caller and
deadline cancellation now run through the host; actual callback/cleanup failures
reach the retained discovery barrier and runtime shutdown. Ordinary handled family
or construction failures remain availability outcomes and do not by themselves
poison otherwise clean teardown. Temporary-directory RED/GREEN pairs use
`tf-slice5-discover-cancel-cleanup`, `tf-slice5-discover-worker-cancel` and
`tf-slice5-discover-worker-cancel-callback` with `-red.log`/`-green.log` suffixes.
The scope-cleanup RED is `tf-slice5-discovery-scope-cleanup-red.log`; the complete
40-case runtime GREEN is `tf-slice5-discovery-final-runtime.log`. The final focused
build reports zero warnings/errors in `tf-slice5-discovery-scope-build.log`.

Review also found that the full solution built the two synthetic package projects
as Debug while `CopySyntheticMod` consumed earlier Release outputs. Both projects
have been added to the solution. The final complete Release build now builds both
synthetic DLLs in Release and passes with zero warnings/errors. The rebuilt
`verify-csharp-release-surface.sh` then passes all eleven SDK packages and all seven
harnesses, including the 40 isolated runtime cases and embedded API baselines.
Solution formatting verification passes. Temporary-directory logs are
`tf-slice5-full-release.log`, `tf-slice5-seven-harnesses.log` and
`tf-slice5-format-verify.log`. Exact-head CI remains pending. The canonical brief
and slice 5/6 prompts include the cleanup/admission and discovery ownership rules.

The pinned Dart conformance entry point passes 579 tests (including the 353 shared
fixture corpus), and domain/data/CLI fatal-info analysis passes. Formatting checked
354 files with zero changes; all 410 tracked non-generated Dart files meet the
500-line cap. README counts, residue, trademark, asset-licence coverage and 125
Markdown-link checks passed during this working-tree review. Final repository
and fixture audits and publication-input checks also pass after the corrections
(25 prepared pages and five snippets from three compiled template sources).
Complete publication and exact-head CI remain pending; local UI Dartdoc has the
separately reproduced toolchain limitation below. These checks do not certify
unrun Flutter, Windows application or game acceptance for this slice. Open Sandbox geometry,
player persistence/readiness, authored markers, native timing and teardown under
in-game fault injection all remain unverified in the installed game.

Activation handoff review also identified a compatibility-audit gap: the Manager
native world adapter is outside the current mod-folder reflection source scan.
The checked-in current-game metadata contains its referenced type names, but some
existing loader bindings omit exact parameter or awaitable return contracts and
one required no-argument constructor is undeclared. Slice 6 must extend the audit
and tighten those bindings before activating these adapters. The green offline
GameCompat count above does not certify these new native reflection paths.

## Explicit release-preparation re-cut

The user selected unsigned Windows x64 `0.1.0-rc.1`. The eight redesign slices
remain required. An additional [release-preparation PR](prompts/07a-release-preparation.md)
follows slice 7 and precedes slice 8's final acceptance candidate. Its separate
scope repairs the candidate qualification cycle and unsigned packaging defects;
it does not weaken any release gate or treat automated tests as game approval.

Read-only review found that `release-admin` requires tracked P0-GAME-01 approval
at the candidate SHA before building the bytes that supply that approval. The
repair separates eligibility for a private build from qualification of exact tested
bytes through detached, reviewed evidence. Four non-game blocking approvals remain
prerequisites; all five blocking gates remain required for publication. Tracked
approval after testing changes the SHA and cannot certify the original candidate.
The release prompt records the bounded contracts and bypass/tamper regression matrix.

The user reports that approval/rotation evidence and isolated QA resources exist;
their concrete locations and host/session identity remain pending. No approval,
evidence reference, credential rotation or game result has been inferred. Unity Hub
3.21.0 is installed, and installed editors include 2022.3.22f1, 2022.3.50f1,
6000.0.31f1, 6000.5.1f1 and 6000.7.0a1. The required authoring editor 6000.0.23f1
is absent at its configured path; this corrects the earlier overly broad observation
that no editor directory existed. No editor was installed. Local Node 24.19.0 is
adequate development evidence but is distinct from the release pin 24.18.0.

## Live acceptance isolation prerequisite

Read-only inspection on 5 September 2026 found the installed Unity 6000.0.31f1
build with Doorstop 4.5.0. Doorstop supports an alternate preloader assembly; the
installed BepInEx preloader derives its root from that assembly, and
`ManagerPaths` follows the loader root. This provides a supported route for
separate loader/manager files, subject to verifying the actual roots at runtime.

It does not isolate Unity native persistent data. The current user has native game
save and authentication files in LocalLow; only filenames were inspected. No
supported save-root override was found in the inspected game metadata. CreatorContent
also reports `persistenceIsolationAvailable: false`. A separate Windows user/session
or VM is therefore the current viable acceptance arrangement; its availability and
game compatibility are unverified. No account, save, authentication file or game
installation was changed, and no game was launched.

The existing `LiveAcceptanceRunner` writes to the ordinary installation even with
`skipRuntimeInstall`, and the Windows stop helper targets every process matching
the executable path. Slices 7/8 must carry an explicit isolated runtime layout,
record actual native persistent-data roots, and retain process ownership including
PID/start time/executable identity. Add tests for unintended ordinary-root writes,
root mismatch, stale acknowledgement and unrelated processes. Abort live setup if
it cannot establish isolation; profile names and environment-variable overrides
alone are insufficient evidence. Visual/input and native-timing acceptance remain
pending, with user assistance required if no suitable native-control surface exists.

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
| GM-10: declared authored spawn ignored | World instance returns resolved spawn; provider validates marker readiness; unsupported dead schema remains excluded | Missing/duplicate marker fails; correct marker resolves before StartAsync; templates use production binding | Authored-marker world starts at intended position; Open Sandbox and discovered providers report actual spawn | 2, 5, 6, 8a, 8b |

Slice 8a adds concrete GM-10 regressions in
[WorldMarkerHierarchyTests](../../../tests/TopiaForge.ModManager.Tests/WorldMarkerHierarchyTests.cs),
[AssetSpawnTransactionTests](../../../tests/TopiaForge.ModManager.Tests/AssetSpawnTransactionTests.cs),
and the template's [WorldValidatorTests](../../../templates/TopiaForge.UnityWorldTemplate/Assets/Tests/Editor/WorldValidatorTests.cs).
The first two run in the ordinary C# harness. The template tests require the pinned
Unity editor and are pending; actual authored spawn placement remains part of 8b
live acceptance. Private admission evidence in 8b also strengthens GM-05's distinction
between process creation, runtime acknowledgement and a committed Running session.


## Replacement slice state

Each slice targets `dev`, is independently green, and starts only after its prerequisite merges.
The old four-PR stack is source material; it does not satisfy these eight delivery boundaries.

| Slice | Deliverable | Implemented | Connected to production | Automated evidence | Live evidence |
| --- | --- | --- | --- | --- | --- |
| 1 | Review/brief/status/prompts and premature-claim corrections | Merged in PR #106 (`f2ec48b`) | Not applicable | Documentation checks and required CI passed at `16c75ba`; baseline Release/seven-harness checks passed | Not applicable |
| 2 | Unused V6 contract/readers/validators/conformance; alias stays V5 | Merged in PR #107 (`c61d5fc`) | Intentionally unused | 149 C# fixtures; 479 domain, 358 data, 227 CLI tests; seven Release harnesses and required CI/publication passed at `055a7d4` | Not required for pure contract |
| 3 | Pure resolution and immutable transport models | Merged in PR #108 (`41f14d0`) | Intentionally no production switch | 353 shared fixtures; 791 domain, 358 data, 227 CLI tests; full Release/seven harnesses and required CI/publication passed at `63540e2` | Not required for pure models |
| 4 | Scoped ownership, lifecycle, shared transition foundations | Merged in PR #109 (`9dc5613`) | Existing V5 scene routes use the shared executor; V6 orchestration activation remains pending | Final head `4b12a1e` passed full CI (`33999792058`), CodeQL (`33999790502`), local Release/seven harnesses and scoped audits | Native behavior pending |
| 5 | Verified bindings, providers/discovery/readiness and native cleanup follow-up | Merged in PR #110 (`b7390fb6`), final reviewed head `7907398c` | Explicit-selection production binder/discovery/adapters exercised by synthetic packages; activation follows in slice 6 | Final exact-head CI `34005261775` and CodeQL `34005260669` passed; fresh local Release and seven harnesses passed | Native world/readiness/timing checks pending |
| 6 | Activate runtime and atomically flip manifests/templates | Working tree based on merged slice 5; uncommitted | Manager composes one orchestrator, migrated declared factories/providers and scoped observers; temporary V3 adapter maps only a unique valid target | 796 domain tests; generated gamemode/world packages pass production install/binding/resolve/start/stop with fake native readiness; combined C# and publication verification in progress | All candidate game acceptance remains pending |
| 7 | Launcher/CLI/overlay preflight, wire V4, observations, durable state | Pending | Pending | Cross-language wire/profile/process/progress integration pending | Cold launch and multi-surface selection pending |
| 7a | Release prerequisites, exact-byte qualification and unsigned Windows repair | Explicit additional PR planned | Pending | Qualification/bypass/tamper and packaging regressions pending | Reviewed evidence and isolated QA locations pending |
| 8 | Retire V5, migration, publication, final acceptance | Original-stack migration/docs need repairs | Pending | Complete scoped matrix, audits, publication and CI pending | Full isolated-profile acceptance pending |

## Slice 5 initial CI and review correction (6 September 2026)

PR #110 initial head `dde58f290cc6470b38fefd77041186ddabd6f9c8` passed full
[CI run 34004356327](https://github.com/Furroxide/TopiaForge/actions/runs/34004356327)
and [CodeQL run 34004355404](https://github.com/Furroxide/TopiaForge/actions/runs/34004355404).
This includes Linux documentation publication, Windows data tests, Flutter checks and
all seven generated templates. It does not establish game readiness or closure of
pre-existing workflow alerts.

Review found world-provider and discovery errors included CLR stack formatting. A
regression first failed, then passed after extracting ordered, deduplicated messages
from every nested primary and cleanup cause. Retained logs are
`%TEMP%/tf-pr110-world-error-messages-red.log` and `...-green.log`. The follow-up source
passed full Release (zero warnings/errors), solution formatting, all 11 SDK packages
and seven rebuilt C# harnesses (`tf-pr110-review-release.log`,
`tf-pr110-review-format-verify.log`, `tf-pr110-review-seven-harnesses.log`). Pinned Dart
3.12.2 toolchain analysis of domain/data/CLI, formatting (294 files, zero changes), tracked
line limits, README/residue/assets/trademark/353-case fixture audits, 125-file Markdown
links and publication preparation passed. These local results require a fresh exact-head
CI run before merge. Strict generated Open Sandbox decoration remains intentional:
incomplete required content fails the declared provider; legacy V5 behavior remains
separate until activation. Geometry and engine readiness remain unverified in-game.

Read-only release review also confirmed workflow alerts #409 and #417 are open on
`dev` `9dc5613`; #416 is fixed on dev but remains open on old main. No alert was
dismissed. The slice 7a prompt records source-repair/reassessment requirements and the
protected release-branch promotion route. It requires final acceptance to bind the
resulting main merge SHA. The slice 7 prompt now records exact installed-profile,
selection, process-ownership, guarded-staging and acknowledgement integration seams.

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

## Slice 5 final integration and slice 6 activation

PR #110 merged normally after all required checks and review threads were resolved. Its final
head `7907398cc9d21dc18d8b9481690dd434f7f30595` passed
[CI 34005261775](https://github.com/Furroxide/TopiaForge/actions/runs/34005261775) and
[CodeQL 34005260669](https://github.com/Furroxide/TopiaForge/actions/runs/34005260669).
The prior slice-5 pending statements above describe their dated intermediate checkpoints.

Slice 6 was created only after that merge, from `b7390fb6`, with no upstream. Local changes activate
the canonical V6 alias, both current-version constants, 17 first-party manifests and all template
outputs together. A fresh selected-package snapshot includes enabled packages excluded by validation
or ordering, retaining their failure evidence instead of shrinking the effective profile. The manager
composes the verified bindings, bounded discovery, one orchestrator and shared native executor.
Worlds' imperative registration/start protocol and GamemodeHost are removed. Free Play has no Sandbox
dependency; Sandbox and Zombies declared factories allocate their controller and resource-producing
facades under the supplied child context. Committed observers expose immutable IDs and bound operations.

The temporary `LegacyWorldLaunchAdapter` is the only old-wire bridge. It accepts a unique valid target
mapping through the resolver, preserves unavailable old values and never maps retired Sandbox to
another mode. Slice 7 must remove it with profile wire V3 and add complete target/override selection,
versioned durable state/observations and request-correlated process/session outcomes. The slice-6 overlay
can launch declared defaults; launcher integration and full release qualification are not complete.

| Slice-6 finding | Regression and local evidence | Limit |
| --- | --- | --- |
| Launcher profile without command reused remembered autoload; safe mode armed a mode | `WorldLaunchArmingTests` captured both failures before repair; complete focused arming suite GREEN | Wire V4 integration remains slice 7 |
| Unknown remembered transition normalized into a different valid choice | Additional arming regression RED then GREEN; saved value now remains invalid | Durable versioned repair UI remains slice 7 |
| Selection omitted enabled packages that failed validation/load order | `RuntimeSessionSelectionTests` RED then GREEN; exact selection, disabled owners and cloned facts preserved | Full launcher pin/installation adapter remains slice 7 |
| Extension factory reused a package context rather than actual child scope | `ContextBoundExtensionTests` RED then GREEN for root/sibling same-owner contexts | Combined lifecycle suite still in progress |
| Sandbox closed a foreign creator host or enabled F5 before Running | Consumer regressions RED then GREEN; checks own host and committed matching session | F5/input timing in game pending |
| Zombies throwing cleanup skipped later resources | Consumer regression RED then GREEN, all cleanup attempts and aggregate errors retained | Native health/teardown verification pending |
| Local import lost ownership or reported success before native terminal failure | Four local import regressions GREEN; captured-root cleanup and actual drain retained | Native importer behavior remains unverified in game |
| Templates compiled without proof of runtime binding | Two generated package cases GREEN through real scaffolder, installer, receipts, binding, resolver and orchestrator; generated source/manifest hashes preserved | Native readiness is a fake boundary, not geometry or spawn evidence |

Current Worlds and SDK acceptance mod Release builds pass with zero warnings/errors. The acceptance
probe preserves all 15 live case IDs and ten lifecycle cycles, uses the actual acceptance factory's
session context, and requires committed Running-to-Idle plus controller/scope disposal before its world
PASS marker. Its resource family is now `session-resources`; no game PASS markers have been produced.
The active authoring guides and website now reference Manifest V6. Combined C# checks, API baselines,
publication, exact-head CI and every live acceptance case are still pending for this working revision.

## Slice6 content/facade/menucleanup verification

These results describe uncommitted source on `feat/gamemode-runtime-activation`,
branched from slice-5 merge `b7390fb6`; they do not certify a release commit or
native game behavior. `ContentAdmissionTests` uses actual scoped `ModContext`
instances, the orchestrator and shared `SceneCoordinator`, with a decorator that
injects reservation/grant/close failures. Its 11 named cases are registered in
both the focused and sequential full harness; the focused cases below passed
against a freshly rebuilt Manager test project.

| Confirmed defect or required boundary | Regression and observed result |
| --- | --- |
| Content reservation exception abandoned the command task and lifecycle lease | `reserve-throw` RED before repair, then GREEN; no callback/child allocation and a subsequent restart is admitted |
| Internal cleanup cancellation replaced an expected `NotFound` with `Cancelled` | `expected-failure` RED then GREEN; original code/message retained, only the failed child closes, terminal session cleanup stays successful |
| Borrowed-grant disposal lost the provider's already-returned content resource | `grant-cleanup` RED then GREEN; ownership is captured before grant disposal, every content/scope disposer runs, all three failure messages remain |
| Worker caller cancellation and stale content callbacks must remain contained | `worker-cancel` and `stale-content` GREEN; cancellation callbacks execute on the host, both throwing callbacks are retained, late content drains while Busy, old scopes cannot stop successor content |
| A child session facade linked restart/menu to the predecessor scope token and cancelled its own teardown | `child-restart` and `child-menu` RED then GREEN; host admission checks the lifetime before invoking the operation, and admitted self-teardown no longer cancels the successor/menu |
| Independent cancellation and a stopped queued caller remain effective | `child-explicit-cancel` reaches the new load and cancels/drains it after the facade fix; `child-queued-stop` remains GREEN and rejects a scope stopped before host admission |
| Main-menu reservation or late close exceptions left command tasks and leases pending | `menu-reserve-throw` and `menu-close-throw` RED then GREEN; Busy persists through held cleanup, primary/close messages are retained, one command outcome and the applicable one predecessor terminal publish, and the next launch succeeds |

Retained local evidence is `%TEMP%/tf-slice6-content-<case>-red.log` and
`...-green.log` using the exact case names above. The three relevant rebuilt
Manager test logs are `tf-slice6-content-build.log`,
`tf-slice6-child-facade-green-build.log` and `tf-slice6-menu-green-build.log`;
each completed with zero warnings and zero errors. The final focused run passed
all 11 cases. No source fix was made for the already-passing worker/stale
boundaries. Complete solution/release-surface and exact-head CI checks are being
run separately; no pending check is represented here as passed.

CLI regressions also captured obsolete `--gamemode` with a missing value creating
files, and V6 migration dispatch rejecting current manifests while mechanical
V3/V4 conversion silently changed to V6. After repair, ten focused scaffold,
no-write and migration cases passed in
`%TEMP%/tf-slice6-cli-scaffold-and-migration-green.log`; failures are retained in
`tf-slice6-cli-obsolete-red.log` and `tf-slice6-cli-migration-dispatch-red.log`.
The full CLI run is still in progress at this checkpoint. The generated C# cases
previously passed actual scaffold/install/receipt/binding/resolve/launch/stop
with fake native readiness; rerunning them with the tracked CLI lock/config
is pending the next C# build slot. Template restart now uses the bound operation's
default token. None of these checks proves scene timing, authored spawn,
Open Sandbox geometry, F5/pause behavior or live teardown.

The first complete CLI run then finished with **231 passed, four existing Windows
skips and one stale world-template assertion failure** in
`%TEMP%/tf-slice6-cli-full-final.log`. The assertion still required the removed
imperative `BundleWorldContent`/`RegisterWorld` source. It now checks the V6
bundle/prefab path, authored `SpawnPoint`, declaration ID and Free Play target;
the focused case passes in `tf-slice6-cli-world-scaffold-green.log`. A complete
rerun remains pending the shared C# build slot because the CLI release-payload
suite itself restores the solution and builds shared SDK/validator projects.
The four unchanged platform skips cover two developer-mode symlink archive
cases, POSIX process signals and a POSIX shell output-overflow fixture.
Final domain/data/CLI analyzers and the standalone generated-helper analyzer
passed with pinned Dart 3.12.2. All 15 owned Dart files are formatted and below
500 lines; the largest remains 496 lines. No new skipped test was introduced.

A later host-queue regression strengthened `stale-content`: an old stop callback
was repeatedly reposted while successor content held Busy. It failed before the
identity check moved ahead of host-side retry. The repaired path dispatches an
off-thread callback once and rejects stale/stopped identity before deferring
valid work. `tf-slice6-stale-content-post-storm-red.log` and `...-green.log`
record the result; `tf-slice6-stale-content-green-build.log` records a fresh
zero-warning/error build. This corrects the earlier narrower stale-handle check,
which proved isolation but did not measure repeated queueing.


## Slice 6 final local verification checkpoint

These results apply to the final uncommitted activation source on
`feat/gamemode-runtime-activation`, based on `b7390fb6`, on 6 September 2026.
They supersede earlier pending local-check statements without erasing their failed
or incomplete checkpoints. The slice is not yet integrated; exact-head CI and
native game acceptance remain separate requirements.

| Check | Observed result |
| --- | --- |
| Full Release solution after the stale-callback fix | PASS, zero warnings/errors; `tf-slice6-final-solution-build.log` |
| Rebuilt SDK packages and sequential C# harnesses | PASS, all 11 packages and all seven no-argument harnesses; `tf-slice6-final-seven-harnesses.log` |
| Production generated packages | Both real scaffolder/install/receipt/binding/resolve/start/stop cases ran in that final Runtime harness, using the tracked CLI lock/config and unchanged generated source/manifest hashes |
| C# format verification | Final PASS after normal post-CLI restore, zero of 1074 files changed and no missing-reference/workspace warning; `tf-slice6-post-cli-format.log` |
| Domain tests | 800 PASS; `tf-slice6-domain-final-tests.log` |
| Complete CLI rerun | 232 PASS, four existing Windows skips; `tf-slice6-cli-full-green.log`. This closes the earlier stale world-template assertion failure, whose failed log remains preserved |
| Windows data tests | 360 PASS, four existing platform skips with the corrected child-process PATH; `tf-slice6-data-clean-path-tests.log` |
| Matching clean Windows baseline | All ten multiplayer pack cases PASS on archived `b7390fb6` using the same PATH; `tf-slice6-baseline-data-pack.log`. The historical seven local failures are no longer an exemption |
| Dart format and analysis | 357 checked domain/data/CLI files, zero format changes; fatal-info analyzers PASS, including the standalone generated-helper analyzer |
| Fixture closure and repository audits | PASS, 355 recursively closed cases, derived README guide count 45, residue/trademark/asset-license audits |
| Full Dart line audit | 416 tracked/new files, all 414 non-generated files at most 500 lines; only two explicitly generated files exceed the cap |
| Flutter tests and fatal-info analysis | UI package 3 PASS; launcher app 54 PASS; both analyzers report no issues using Flutter 3.44.6 |
| Windows launcher debug build | PASS; `tf-slice6-flutter-windows-build.log`. The executable was built, not launched for game acceptance |
| Website checks | 33 tool tests, 26 canonical pages, 5 snippets from 3 compiled template projects, Markdown links over 126 files, Astro analysis and 27-page build PASS |
| C# API publication | DocFX PASS, 381 HTML pages, zero warnings/errors |
| Dart API publication | Domain/data PASS with zero warnings/errors; UI fails in pinned dartdoc 9.0.4 with the documented Flutter SDK CRLF `DocumentationComment._stripDocImports` RangeError. No SDK/cache patch or check weakening; exact-head Linux publication is required |

The installation compatibility regression now preserves and checks every supported
content target rather than one enumeration choice; two shared fixtures prove both
native-world and code-provider cases. Duplicate selected identities have no binding
winner. Malformed enabled selections remain attributable diagnostics and block scene
work; disabled malformed selections do not block unrelated valid gameplay. Runtime
integration tests exercise healthy package loading alongside both failure forms.

The strict metadata baseline covers 60 types and 15 simple lookups. Across nine
binding manifests, 203 of 214 bindings verify and 11 legacy dynamic bindings remain
explicitly uncheckable; all 29 Manager and 35 Worlds bindings verify. Fourteen new
metadata regressions enforce exact overloads, fields, properties, visibility and
awaiter shape. Source auditing reports no undeclared or stale bindings. These are
metadata and controlled-runtime results, not native timing evidence.

No game was run for these checks. Open Sandbox geometry/environment/kill plane,
actual spawn and discovered scene readiness, Zombies, Sandbox F5/pause, Free Play
without Sandbox, restart/menu, and injected native startup/teardown remain pending
live acceptance in an isolated environment. Launcher wire V4, correlated outcomes,
versioned selection repair, V5 retirement and release qualification remain later
slices. No release dispatch or publication has been performed.


The final publication run generated 381 C# API pages with zero DocFX warnings/errors.
The missing UI Dartdoc output then correctly failed unified search and produced 25
built-link failures, all pointing at the absent `/api/dart/` landing output. Other
built destinations and anchors passed. Full publication is not locally green;
Linux CI must establish it on the committed source. Logs are
`tf-slice6-docs-dart-reference.log`, `tf-slice6-docs-build-search.log`, and
`tf-slice6-docs-check-built-links.log` in the temporary directory.

The format workspace warning was traced to the CLI relocation test overwriting
ignored NuGet assets with its subsequently deleted temporary package-cache path.
Analyzer project and toolchain configuration match `b7390fb6`; the normal NuGet
cache contains the package. A normal solution restore after the CLI test repairs
those local asset paths, without changing source, SDK caches, or verification rules.

The post-CLI solution restore and final diagnostic format check passed: zero of
1074 files changed, with no missing-reference or workspace warning. The prior
warning is retained above as an explained intermediate result.


## Slice 6 PR review checkpoint

[PR #111](https://github.com/Furroxide/TopiaForge/pull/111) was opened at
`c8a982c159bfd8fe5b6f6b2ba8ca4e8d190e142b`. Exact-head
[CI 34010461932](https://github.com/Furroxide/TopiaForge/actions/runs/34010461932)
passed every functional job, including Windows data/Flutter checks, all seven
compiled templates, and complete Linux guide/C#/Dart/search publication. This
establishes publication for that committed source despite the reproduced local
Windows Dartdoc defect; it does not certify a later follow-up commit.

CodeQL reported new path flows in reflection-audit fixtures and an externally
selected executable in generated acceptance (alerts 433-442). Review confirmed
five real source-audit directory-link escapes on Windows; each failed before
repair with no skipped cases. Follow-up fixes use owned temporary directories,
repository/ancestor containment and a closed SDK tool choice. Arbitrary executable
overrides fail before execution; generated gamemode and world acceptance then
passed with the fixed repository FVM SDK. No alert was suppressed or dismissed.
Follow-up exact-head CodeQL, functional CI and review resolution remain pending.

Additional review identified Idle pause exit incorrectly reported as Busy and
acceptance startup ignoring an independently cancelled session token. Both failed
before repair; the rebuilt seven-case Worlds suite and five-case actual acceptance
factory suite now pass. Null acceptance sessions receive an actionable argument
error; ordinary missing-probe failures retain Unavailable. These suites are registered
in the sequential full harness. Logs are under `.dart_tool/slice6-copilot-*`.
Native game evidence and release qualification remain pending.


A final independent review found delayed autoload could resume after a later
explicit launch or main-menu operation. The temporary discovery adapter must
serialize its generation check and both explicit commands on the host dispatcher,
so waiting or queued old work cannot override a committed newer choice. Five controlled
discovery/host-queue regressions failed before repair (menu, target, queued-menu,
queued-target, worker-explicit). All eight discovery cases now pass; the plugin
routes both explicit entry points through the same host-serialized generation gate.
Logs are `tf-slice6-legacy-<case>-red.log` and `tf-slice6-legacy-admission-green.log`.
Removing the temporary adapter in slice 7 must preserve this command-precedence invariant.


The combined review follow-up Release solution passes with zero warnings/errors
(`tf-slice6-review-solution-build.log`). Final format verification changed zero
of 1075 files. Diagnostic output includes six Debug metadata-mapping notices;
every referenced project loaded and ran analyzers, with no missing package,
failed project load, or required-reference error. These notices are distinct from
the earlier deleted CLI-cache failure. Dart format checked 358 files with zero
changes; fatal-info analysis and all repository/fixture/Markdown audits pass.
The complete rebuilt verification passed on this combined source: all 11 SDK
packages and all seven sequential harnesses (`tf-slice6-review-seven-harnesses.log`).
Exact-head functional CI and CodeQL remain required after the follow-up commit.


## Slice 6 final integration and slice 7 start

PR [#111](https://github.com/Furroxide/TopiaForge/pull/111) merged normally into
`dev` at `bd7be8be386c51713fa1099488e1e4d36ba130a5` on 2026-09-06T04:46:28Z.
The reviewed head was `584349c123c72075b8922093a9486ae0491d041d`.
[CI 34011825891](https://github.com/Furroxide/TopiaForge/actions/runs/34011825891)
passed every required job, including C#, both Windows and Linux launcher_data,
Flutter, all seven templates, and complete Linux documentation publication.
[CodeQL 34011825742](https://github.com/Furroxide/TopiaForge/actions/runs/34011825742)
passed at that same head; all ten findings 433–442 have `fixed` instances on
`refs/pull/111/head`. None were dismissed or suppressed. All review threads were
resolved after source repairs and regression evidence. A cancelled duplicate policy
run reported failure because its prerequisite was cancelled; the replacement
required policy check passed, and normal merge eligibility was clean.

These results supersede the pending hosted-check statements in the dated slice-6
checkpoints above. The local Windows Dartdoc 9.0.4 CRLF failure remains recorded;
the exact-head Linux publication completed successfully without patching the SDK.
They establish implementation, production connection and automated verification,
not Unity scene timing, generated geometry, authored spawn correctness, or live
teardown behavior. No game or release was launched to obtain these results.

Slice 7 was branched from the verified merge only after it landed. Early regression
work has reproduced legacy profile value loss, per-profile Home gating mistakes,
unsafe staging ancestors, unpinned package selection drift and request activity
correlation failures. Repairs and focused tests are being combined; the slice is
not yet committed, independently green, integrated, or game-verified. Release
preparation (7a), V5 retirement (8), isolated game evidence and final qualification
remain mandatory before a release claim.


## Slice 7 working-tree review, 8 September

The branch remains based on `bd7be8be`; this checkpoint is uncommitted source,
not an integrated or release-qualified revision. V4 production intake, target
controls, exact preflight and correlated activity are connected in the working
tree. V3 command readers and the temporary startup adapter are removed. The
malformed-state recovery and final process creation changes are under final checks.

| Area | Implemented / connected evidence | Automated evidence at this checkpoint | Game-verified |
| --- | --- | --- | --- |
| Shared profile/target contract | Strict raw selections, immutable previews, policy-filtered worlds/transitions and versioned revisions | Full domain 867 passed before the final integer-selection-version fix; that final focused suite passed 8 cases | Pending |
| Launcher UI | Per-profile Home gating, Home/Setup/Profiles target controls, repair states and receipt-bound activity | Full app 75 passed; extra stale/foreign/process-exit, exception and 200% text regressions failed before their fixes | Pending |
| Data admission | Exact installed package snapshots, duplicate/malformed diagnostics, final preflight, guarded cross-instance lease and stale-save fencing | Clean `bd7be8be` multiplayer baseline 10 passed under the same short Windows PATH; current full data passed 423 before later review additions | Pending |
| Runtime composition | V4 request intake, production binder/orchestrator, guarded observations/outcomes, overlay controls and read-only malformed-state recovery | Rebuilt solution: zero warnings/errors; 11 SDK packages plus all 7 harnesses passed; final raw-state identity fix followed by full rebuild and manager run | Pending |
| Windows process ownership | Original suspended creation handle supplies PID, image and native creation token; restart verifies one held generation | 8 harmless-child creator/control cases passed, including arguments/environment/cwd and detached survival | Pending |
| Process admission probes | Failed/unreadable queries remain unknown; canonical Windows aliases and relative native/Wine targets cannot establish false absence | 13 probe regressions/control cases passed; Linux process-epoch plus native control coverage passed separately | Pending |

The earlier Windows multiplayer failures were reproduced as tool PATH pollution,
then cleared on the matching clean baseline. They are not a standing exemption.
Four platform/privilege skips in earlier broad runs are recorded as skips, not
passes. Full CLI, final data/UI checks, Windows build and current-head publication
are still required. Exact-head hosted CI and CodeQL cannot be claimed before a
commit and PR exist.

Review also found unsafe initial ownership from detached PID lookup, hidden
Loading/Starting progress, stale callbacks accepted without a receipt, lost profile
values during coercion, simultaneous-launch races, and stale full-list saves that
could erase or resurrect profiles. Each repair has a preceding failing regression.
Evidence lives in `tf-slice7-*` temporary logs and the checked-in test cases; native
identity/resume failure cleanup is implemented but not yet fault-injected.

No game, normal-user save/authentication data, release tag, release dispatch or
published package was used for these results. The isolated game acceptance matrix,
release-preparation slice 7a and V5-retirement slice 8 remain pending.


## Slice 7 final local verification, 8 September

This checkpoint supersedes the earlier partial-suite counts. It describes the
working tree based on `bd7be8be`, before its commit and hosted checks. Production
Home, Setup, Profiles, CLI and manager overlay use target selection and the same
resolver; both process producer and runtime consumer use wire V4. The original
Windows creation handle establishes restart ownership. A detached PID lookup does
not establish that ownership. Linux creation currently returns an unverified
receipt, so the launcher will not claim authority to terminate it.

| Verification | Result |
| --- | --- |
| Domain | 868 passed; fatal-info analysis clean; a later test-file split passed its 58 affected cases |
| Windows data | 469 passed, 4 existing symlink-privilege skips; fatal-info analysis clean |
| CLI and generated SDK packages | 266 passed, 4 documented platform skips; all 7 templates passed both relocation paths; fatal-info analysis clean |
| Flutter | App 75 and shared UI 3 passed; both analyzers clean; Windows x64 debug build passed |
| C# | Final Release solution: 0 warnings/errors; 11 SDK package audits and all 7 rebuilt sequential harnesses passed, including publication retries |
| Dart formatting and size | 477 files checked, 0 changes; 476 non-generated files checked, none above 500 lines |
| Repository audits | README counts, rename residue, trademark and asset-licence checks passed |
| Website sources | 33 unit tests, 126 Markdown files and 521 repository data files passed; Astro checked 26 files with 0 errors/warnings/hints; guide build produced 27 pages |
| API publication | C# reference produced 381 HTML pages with 0 warnings/errors; domain/data Dart references generated; UI Dart reference failed as detailed below |

The final data regressions prevent `loadSnapshot` from rewriting malformed manager
state during restart reconciliation, reject duplicate properties and lossy numeric
normalization before decoding, preserve current selection presence, and match the
runtime's 4 MiB manager-state bound. A missing primary with an existing backup
cannot silently become empty state. Explicit safe mode remains a read-only empty
main-menu recovery; ordinary launch reports repair requirements. Native fixture
teardown now waits for its recorded process generation to exit before removing the
working directory. The complete concurrent data run includes that regression.

Local publication remains incomplete because the unmodified pinned Flutter 3.44.6
Dartdoc 9.0.4 fails in `_stripDocImports` with a CRLF-related RangeError while
processing launcher_ui. Unified search correctly rejects the missing searchable
UI reference; built-link verification reports 25 missing `/api/dart/` landing
links. This reproduces the recorded slice-6 tool failure. It is not a publication
pass or permission to skip exact-head Linux publication. No SDK cache was patched.

Logs are in the temporary directory under `tf-slice7-*`, including
`domain-final`, `cli-full-current`, `flutter-app-full-current`,
`flutter-ui-full-current`, `flutter-windows-debug-current`, `dart-format-final`,
`astro-check`, `docs-csharp`, `docs-dart`, `docs-search` and `docs-built-links`.
The final data logs are
`packages/launcher_data/.dart_tool/slice7-data-final-complete*.log`.
These ignored machine-local logs supplement checked-in regressions; hosted evidence
must identify the actual committed revision before integration.

Implemented and connected: slice-7 launch paths and durable/one-shot contracts.
Automated-tested: the results above; exact-head hosted checks remain pending at
this checkpoint. Game-verified: pending in every area. No game,
normal-user save/authentication data, release tag, dispatch or package publication
was used. Native identity/resume fault injection, isolated gameplay acceptance,
release preparation (7a), V5 retirement (8), and final qualification remain open.


The final runtime follow-up reproduced two publication defects before repair:
a failed observation write suppressed retries until the registry changed, and
one-shot progress/outcome write failures lost those records permanently. The
publisher now keeps the latest progress and one immutable record per outcome
channel, retries on monotonic bounded intervals even with an unchanged registry,
and attempts one final disposal flush. Successful channels are not rewritten;
retries never emit lifecycle notifications. All seven publication scenarios and
the complete rebuilt release harness passed on the combined source. Logs:
`tf-slice7-observation-retry-{red,green}.log`,
`tf-slice7-terminal-retry-{red,green,build}.log`, and
`tf-slice7-final-seven-harness.log` in the temporary directory.

The final public-guide review corrected retired CLI flags, acknowledgement exit
codes, required world-play targets, and prefab export instructions. The guide
build passed again after those edits. The next prompts now identify the remaining
authoring marker-validation gap and distinguish already-completed unsigned P7S
omission from the outstanding release qualification/signing work. These are
handoff instructions, not claims that slice 7a or 8 is implemented.


Final full C# format verification exited 0 and changed 0 of 1095 files. Its six
Debug project/metadata mapping notices match the previously explained workspace
notices; all projects loaded and analyzers ran, with no missing package or failed
project load. The actual SDK is 10.0.301. The format host reports runtime 10.0.11,
which is also installed alongside 10.0.9; these development checks do not certify
the release builder's pinned runtime. Release preparation must verify its actual
frozen toolchain independently. Remote `dev` remained `bd7be8be` after the final
build, and all pre-push repository/Markdown audits passed again.


## Slice 7 hosted review and fixture corrections

[PR #112](https://github.com/Furroxide/TopiaForge/pull/112) opened at
`c75f3d2378102eb63a8cf1abf6d24df3da0ddb13`, based on `bd7be8be`.
[CI 34167161651](https://github.com/Furroxide/TopiaForge/actions/runs/34167161651)
passed C#, Flutter, all seven templates, repository hygiene, Linux domain (868),
Linux CLI (270), Windows data (469 with four skips), and complete Linux
[guide/API/search publication](https://github.com/Furroxide/TopiaForge/actions/runs/34167161651/job/101881159768).
The overall run failed solely on two Linux data process-identity fixture cases
(462 passed, two failed, nine skipped). These are actual failures, not exemptions.

An isolated Ubuntu 24.04 run with native Dart 3.12.2 reproduced both failures.
Dart's frontend changes the child image to `dartvm`; the test incorrectly expected
the frontend image. Production image/generation comparison correctly rejected it.
The fixture now launches the VM directly, as the Windows creation fixture already
did. The same Linux control/epoch suite went from six passed/two failed to eight
passed; the Windows creator/control/epoch suite passed all twelve cases. Production
process ownership checks are unchanged. Logs:
`%TEMP%/topiaforge-native-linux-zTuHF0/native-{before,after}.log` and
`%TEMP%/tf-slice7-native-linux-fixture-windows.log`.

The C# CodeQL SARIF analysis `1737970295` reported fifteen path findings
[443–457](https://github.com/Furroxide/TopiaForge/security/code-scanning/443).
Every trace originates at one of two test `Path.GetTempPath` roots, including
flows into already-guarded production sinks. Negative containment/link review,
including a dangling ancestor link, found no demonstrated production traversal
bypass. Those fixtures now use `Directory.CreateTempSubdirectory` for atomic
ownership. Production guards remain intact. The rebuilt solution and all eleven
SDK/seven-harness checks passed after that repair. No alert was suppressed or
dismissed; the next-head analysis must establish fixed instances. Logs:
`tf-slice7-c75f3d2-codeql-csharp.sarif` and
`tf-slice7-codeql-fixture-{format,build,seven-harness}.log` in the temporary directory.

Review also identified two corrupted documentation dashes; both were repaired,
along with the same encoding artifact in the log-truncation marker. These fixture
and text corrections require fresh hosted evidence before merge. The successful
publication above certifies `c75f3d2`, not an untested follow-up revision. Live-game
and release qualification remain pending.


## Slice 7 merged; slice 7a active

PR #112 merged normally on 7 September at 22:58:39 UTC as
`da47dc7f89462c4473bac54db7e1c98acecda52d`, preserving reviewed head
`7bc19231bf6ea12bb708ef318774d78fffee72cd`. Final
[CI 34167871674](https://github.com/Furroxide/TopiaForge/actions/runs/34167871674)
and [CodeQL 34167870357](https://github.com/Furroxide/TopiaForge/actions/runs/34167870357)
passed. This includes rebuilt C#/SDK/extracted-template checks, Linux domain 868,
Linux data 464 plus nine skips, Windows data 469 plus four skips, Linux CLI 270,
Flutter, templates, hygiene, and complete Linux documentation publication.
C# analysis 1737999993 returned zero results on the exact reviewed head; all
fifteen alerts 443–457 are fixed on that PR ref, with no dismissals. The merge
used ordinary branch policy, with no administrator bypass.

The sequential `feat/release-candidate-qualification` branch was created without
an upstream from that refreshed `origin/dev` only after the merge. Slice 7a is
in progress; no release tags, dispatches, publication, or live game runs occurred.
Seven new prerequisite CLI regressions first failed for the actual missing
private assessment and legacy no-assets publication behavior, then passed.
Separate duplicate-readiness-property regressions reproduced silent acceptance
before parser hardening. Logs use `tf-slice7a-prerequisites-{red,green}.log` and
`tf-slice7a-readiness-strict-red.log` in the temporary directory. Earlier setup
failures are not regression evidence.

Automatic approval review refused the unsigned policy/build mutation because it
could not establish explicit user authorization from the available context.
The user has been asked directly; that mutation remains pending, while unrelated
qualification, workflow-security and dependency work proceeds. Existing approval
and credential-rotation records and the isolated QA host/session locations remain
unsupplied. No synthetic test approval is production evidence. Game verification,
release qualification and slice 8 remain pending.


## Slice 7a qualification checkpoint, 8 September

This checkpoint binds qualification to exact Git blobs and staged bytes. It adds
strict detached decision/acceptance schemas, the fixed 36-case acceptance inventory,
private-build prerequisite assessment, immutable qualification summaries, BOM v4,
and repeated validation across administrator and hosted approval boundaries.
Source checks resist Git replacement refs and index hints, duplicate JSON keys,
metadata-path substitution, payload/embedded-package disagreement, and changes
between validation and publication. Signed P7S identity remains bound when the
selected policy requires it. Hashes establish identity, not human approval.

Workflow repairs constrain source provenance, remove unused arbitrary-source
inputs, and use restore-only Flutter caching. Four compatible website dependency
patches remove the recorded audit findings. Exact-head hosted CodeQL must confirm
new alert instances; no alert dismissal is evidence of a fix.

This is a draft review checkpoint of the completed qualification/workflow/docs
work, not the completed release-preparation slice. Three pending unsigned test
files are retained locally and excluded from this checkpoint commit:

- `apps/topiaforge_cli/test/release_package_builder_test.dart` (two-line registration).
- `apps/topiaforge_cli/test/release_package_builder_unsigned_cases.dart`.
- `tools/release/test-windows-signing-policy.ps1`.

The corresponding three-file production proposal is preserved as an inactive
patch under the ignored `.dart_tool` directory. It has not been applied, compiled
or behavior-tested. Its SHA-256 is
`5f9262629668a2c745fef38c08a00686fedf4a24eeaa525ae2a569869ad72a48`.
The registered local tests remain failing; excluding their unfinished work from a
draft checkpoint does not waive them or qualify the complete working tree.
Production release policy and Windows signing behavior remain unchanged.

| Verification | Recorded local evidence | Limit |
| --- | --- | --- |
| Fresh Release build | Zero warnings/errors; `tf-slice7a-csharp-build.log` | SDK 10.0.301; installed development runtime 10.0.11 does not certify the release runtime pin |
| Rebuilt C# release surface | Eleven SDK packages and all seven harnesses passed; `tf-slice7a-seven-harness-final.log` | No live scene, player, spawn or teardown evidence |
| Complete CLI working-tree suite | 376 passed, four platform skips, three failures; `tf-slice7a-cli-full.log` | The failures are the pending unsigned regressions; this is not a green full-worktree claim |
| Candidate acceptance and signing shapes | 54 acceptance cases and four signing-shape cases passed | Synthetic packages, signatures and approvals only |
| Frozen source contracts | 32 cases passed; `tf-slice7a-frozen-source-green.log` | Exact Git inputs, not real release approval |
| Metadata source content | Fourteen focused and fourteen adjacent cases passed; CLI `.dart_tool/slice7a-metadata-source-*-green.log` | Includes index-hint and hydrated-LFS regressions in owned temporary repositories |
| Administrator and publication | Mock lifecycle, qualification, stdout capture, asset ownership and approval-boundary tampering tests passed | No real staging, dispatch or publication |
| Website | 37 tests passed; npm audit reported zero vulnerabilities | Locked dependencies, not release package qualification |
| Windows documentation publication | Guides, C# API, domain/data Dartdoc passed; launcher UI Dartdoc hit the recorded SDK-comment RangeError | Full Linux publication required on this checkpoint head; no SDK patch or exemption |
| Repository audits | Residue, trademark, asset licence, counts, Markdown and guide catalog checks passed; 83 audit regression tests passed | Counts derived from the actual tree; no game acceptance |

Logs beginning `tf-slice7a-` above are in the local temporary directory unless a
repository-relative location is given. Final checkpoint-specific test and hosted
results must be appended after they finish. Four required non-game approval and
credential-rotation records, plus an isolated QA host/session with explicit paths,
remain unsupplied. All live acceptance and final release decisions remain pending.


Final combined qualification verification passed after all source repairs: 138
cases across 17 selected CLI test files, with zero failures or skips. Full CLI
`analyze --fatal-infos` reported no issues, and `format --output=none
--set-exit-if-changed .` checked 165 files with zero changes. Exact arguments and
logs are under `apps/topiaforge_cli/.dart_tool/slice7a-cli-final-*`. This focused
run excludes the separately recorded unfinished unsigned-builder suite; its
failures remain unresolved. All tracked and new non-generated Dart files satisfy
the 500-line limit.

The final workflow review passed three source-trust tests, the pinned restore-only
Flutter cache contract, eight Pages/dependency cases and actionlint 1.7.7 across
all six edited workflows. Local actionlint disabled external ShellCheck and
Pyflakes; hosted checks remain required. A fresh lockfile audit still reported
zero vulnerabilities. README counts, residue, trademark, asset-licence, Markdown
(126 files), guide catalog (26 pages, five snippets) and whitespace checks passed
again after the current-state documentation update.


## Slice 7a first hosted checkpoint

[Draft PR #114](https://github.com/Furroxide/TopiaForge/pull/114) opened at signed
commit `5cc2bda3afb60de3185aa8a092bf80408794cf20`, targeting `dev` at `da47dc7f`.
[CI 34172424504](https://github.com/Furroxide/TopiaForge/actions/runs/34172424504)
passed every job, including rebuilt C#, all seven templates, Linux CLI (393 tests),
domain/data, Windows data, Flutter, hygiene, and complete Linux guide/C#/Dart/search
publication. Dependency review, PR policy, registry and Unity source validation
also passed. This independently certifies the committed checkpoint, not the three
unfinished local unsigned regression files. After the long build, refreshed
`origin/dev` remained `da47dc7f`.

[CodeQL workflow 34172423419](https://github.com/Furroxide/TopiaForge/actions/runs/34172423419)
completed successfully, but the separate **CodeQL gate failed**. Analysis
1738183793 (Actions) returned three findings: open #409 and historically dismissed
#1/#2. Its trace flows from the Pages `workflow_run.head_sha` checkout into build
steps while identifying the separate `workflow_dispatch` event context. Review
found mutually exclusive event guards, not a demonstrated executable bypass;
nevertheless #409 remains open and is not waived. C# analysis 1738188633,
JavaScript/TypeScript 1738184121 and C/C++ 1738183971 returned zero results on this
head. #416/#417 were absent from the Actions results at this PR ref; that does not
close their older default-branch instances. No alert was dismissed or suppressed.

The follow-up narrows the Pages CI refresh to protected `refs/heads/main` and
requires its checked-out commit to equal the successful CI head before repository
code executes. A delayed CI completion for an older main commit must fail rather
than replace newer documentation. Exact-head regressions, workflow checks and a
new CodeQL analysis must establish the repaired result; the initial successful
build does not certify this follow-up source.

GitHub reported eight open dependency advisories on the default branch. They all
refer to the four website packages patched in this checkpoint. The clean local
lockfile audit and passing dependency review do not substitute for promotion and
fresh default-branch evidence. No release preparation approval or game acceptance
has been manufactured from these test results.


First-head Actions evidence is retained in the local temporary directory as
`tf-pr114-actions-1738183793.sarif.json`, `tf-pr114-alerts.json` and
`tf-pr114-actions-per-ref-instances.json`. The last file distinguishes main, dev
and PR instances; global dismissal state and raw per-ref instance state are not
interchangeable. Hosted CLI and publication logs are
`tf-slice7a-114-{cli,publication}-hosted.log`.


Before the Pages follow-up push, the unchanged release-surface script passed
again: all eleven SDK projects rebuilt and packed without warnings, followed by
all seven no-argument C# harnesses. The final log is
`tf-slice7a-pages-followup-seven-harness.log` in the local temporary directory.
An earlier invocation used incorrect MSYS path-conversion settings, so Bash
could not enumerate the eleven packages written by dotnet; that setup failure
is retained separately as `tf-slice7a-pages-followup-seven-harness-path-mismatch.log`.
The corrected invocation changed no source or test expectation.


The new Pages regressions failed before the guard existed: Python reported one
failure and two missing-step errors across five cases; Node reported two failures
across five cases. After the protected-main checkout and exact-head guard were
added, all five Python cases passed, including seven executed Bash scenarios for
matching, stale and malformed CI heads. All 38 website tests, the Flutter cache
contract and actionlint across six changed workflows passed. Logs in the local
temporary directory use `tf-pr114-protected-main-{red,green}.log`,
`tf-pr114-pages-main-{red,green}.log` and `tf-pr114-pages-full-green.log`.
This establishes local source behavior; the next hosted CodeQL gate remains
required. The unsigned proposal and production signing source hashes are unchanged.


## Open PR comment review, 8 September

The requested audit covered all eight open PRs authored by the project account:
#68, #92, #99, #102, #103, #104, #105 and #114. It read conversation comments,
review bodies (including suppressed findings), and all review threads, with no
remaining pagination. Ten unresolved inline threads were on the superseded
#102-105 stack. Historical branches remain preserved rather than reconstructed.

The ASCII identifier/type findings in #102 were fixed by merged #107 (`c61d5fc`).
The #103 Sandbox registration ownership and Zombies stale-session path findings
were fixed in #111 (`c8a982c`, merged `bd7be8b`). All three #104 owner-selection
threads, including suppressed ambiguity diagnostics, were fixed by #108 (`41f14d0`).
The standalone module guide finding in #105 was fixed by #111. Replies must point
to these actual replacements; none establishes live gameplay acceptance.

Review found a remaining current-guide defect: Modding still called V5 canonical.
Related current instructions in ModPackaging and Sandbox also named V5. Three new
cross-artifact website regressions first failed against the actual V6 alias and
Sandbox manifest, then passed after the prose was corrected. Modding's template
table now describes declared targets/controllers and session-owned world loading.
The full website suite passed all 41 cases. Logs:
`tf-pr-comments-authoring-{red,green}.log` and `tf-pr-comments-website.log` in the
local temporary directory.

Tracing #102's Unicode coverage also exposed a masked negative fixture: an
unrelated launch target named a nonexistent gamemode. A new control test first
failed when replacing only the invalid Unicode ID with valid ASCII. Removing
that unrelated target makes the identifier the sole defect. The original fixture
still rejects; repairing only its ID now passes both schema and reader checks.
All 585 focused Dart cases, 356 C# conformance cases, fixture closure, analysis and
formatting passed. Logs: `tf-pr102-unicode-fixture-{red,green,csharp}.log`.

Two #105 threads remain open and explicitly carried in the slice-8 prompt: V4
migration guidance must change with the actual V6 migration path, and the existing
V5 acceptance test must change when V5 is retired. Their current implementations
are consistent with temporary V5 support; claiming them fixed would be premature.
The suppressed historical session-fixture schema mismatch is also recorded there.

PR #68 has separate review-driven transport/disclosure repairs in its own isolated
checkout; they are not part of this qualification branch. #92 remains constrained
by the existing release-only route into main; its Dependabot guards already exist
on dev and must be carried by normal promotion. The #99 helper issue was already
fixed at its reviewed head. #114 had no actionable reviewer comments at inventory.
No unsigned policy, approval evidence or game acceptance changed during this audit.


The required pre-push release-surface check passed again with all eleven SDK
packages and all seven C# harnesses; see `tf-pr-comments-seven-harness.log` in the
local temporary directory. Its first attempt correctly rejected duplicate source
references because the separate PR #68 checkout had initially been nested under
this worktree. That owned checkout was relocated, with both agents paused, to an
isolated temporary directory; the unchanged audit then passed. The initial log
is preserved as `tf-pr-comments-seven-harness-nested-checkout.log`. No source-census
rule or test expectation was weakened to accommodate the second checkout.


## Authorized unsigned construction, 8 September

The user explicitly answered **Authorize unsigned Windows RC1** to the concrete
permission question. That supersedes the earlier automatic approval-review
rejection and pending-permission handoffs. The authorization covers removing
Authenticode for Windows `0.1.0-rc.1`; Ed25519 update signing, checksums, provenance,
protected approval and the five blocking release gates remain required. It does
not supply missing reviewer records or establish native data isolation.

The preceding committed checkpoint `4000af765dfc8ca7981a3857dde6099f36057926`
passed all 31 hosted checks, including full Linux documentation publication in
CI [34175052857](https://github.com/Furroxide/TopiaForge/actions/runs/34175052857)
and CodeQL [34175050957](https://github.com/Furroxide/TopiaForge/actions/runs/34175050957).
Exact-head Actions, C#, JavaScript and C/C++ analyses returned zero findings
(1738292265, 1738296600, 1738292611 and 1738292852). This supersedes the earlier
pending-hosted-check statements; it does not certify the subsequent unsigned edits.

Before changing production, the expanded builder suite recorded 8 passes and
21 failures: 18 new policy/output boundary cases and the three original unsigned
defects. The first PowerShell expansion recorded 20 passes and 33 failures,
including version parity and output-write ordering. Logs are retained under
`apps/topiaforge_cli/.dart_tool/slice7a-unsigned-boundaries-red.log` and
`.dart_tool/slice7a-windows-signing-expanded-red.log`. Further raw-presence cases
and final combined verification remain in progress.


The subsequent raw-presence suite reproduced three more failures (14 passes,
three failures): explicit null mode, null certificate and empty certificate
could delete prior staging. Raw-type validation now occurs before defaults or
writes. The final PowerShell pre-fix matrix recorded 25 passes and 38 failures
in `.dart_tool/slice7a-windows-signing-strict-red.log`. Line-ending cases were
added after the production repair and are positive coverage, not claimed RED
history. A final empty-object strict-mode diagnostic failure was then repaired.
All 68 PowerShell cases pass in `tf-slice7a-authorized-windows-final.log` under
the local temporary directory; the no-argument suite is registered in CI.

The first combined CLI run recorded 402 passes, four platform skips and 27
failures. Eight failures exposed older handoff fixtures that implicitly consumed
the repository signing mode; they now use an explicit signed fixture while
retaining every signature/negative assertion. The remaining failures began with
30-second timeouts during concurrent Git/SDK fixture work; subsequent missing
fixture paths followed timed-out teardown. These are preserved in
`tf-slice7a-authorized-cli-full.log`, not treated as a permanent exemption.
Focused repaired handoff/builder suites pass all 87 cases. A complete rerun with
two concurrent suites retains every assertion and deadline and is in progress.

All 166 CLI source/test files pass format checking; full CLI analysis reports no
issues. The mandatory release-surface script rebuilt and verified eleven SDK
packages and all seven C# harnesses (`tf-slice7a-authorized-seven-harness.log`).
No C# or SDK source changed in this follow-up.

Node 24.18.0 is now provisioned in the ignored local tools directory. The
Windows x64 archive matches the official published SHA-256
`0ae68406b42d7725661da979b1403ec9926da205c6770827f33aac9d8f26e821`;
`node.exe --version` reports `v24.18.0`. This is checksum verification, not a
claim of detached-signature verification. The local provisioning record is
`.dart_tool/release-toolchains/node-v24.18.0-provisioning.json`. On this pinned
Node version, all 50 website tests pass; Astro checks 29 files without findings
and builds 27 pages. Markdown, JSON/YAML, catalog, README count, residue,
trademark and asset-license audits pass. Full Linux/API publication is still
required against the final pushed head.

The non-game approval/rotation record locations and isolated Windows QA identity
remain pending user input. No game or candidate was run, no credentials were
read, and no release qualification, tag or publication was performed.


Final local verification for the authorized unsigned follow-up is green.
The complete CLI suite, run with two concurrent suites after other heavy test
lanes finished, passed **431 tests with four platform-specific skips** in
3 minutes 45 seconds (`tf-slice7a-authorized-cli-green.log`). Every assertion and
deadline remains enabled. The earlier two-concurrent-suite run recorded 428
passes, four skips and one metadata-test timeout; that log remains retained.
A clean detached `4000af7` checkout and the current checkout both passed the
unchanged metadata file in isolation (two tests each; its first case took six
seconds). No unsigned slowdown or clean-baseline full-suite timeout was
established. Its three independent behaviors are now separate tests, preserving
all assertions and successful-load-then-tamper checks; all four cases pass.

Administrator orchestration and nested qualification, publication staging/
finalization/immutable reruns, and attestation positive/negative suites passed
with synthetic fixtures. The real-root administrator assertion now names the
explicit unsigned RC1 policy; independent signed fixtures remain intact. Its
pre-fix failure and final logs are preserved under `.dart_tool/slice7a-release-*`.
The final 68-case admission run also passed after output-style cleanup. All
changed PowerShell files pass the existing repository analyzer configuration,
and all five workflow source-trust tests pass. All **511 non-generated tracked
Dart files** are at most 500 lines.

This completes local source verification for slice 7a. Fresh CI and CodeQL must
certify the pushed follow-up before normal integration. Slice 8 has not begun;
its read-only port map is preserved at `.dart_tool/slice8-port-repair-plan.md`.
No exact candidate build, isolated gameplay acceptance or publication is claimed.


The unsigned implementation at `eb54909535bf6d47a096904472b76396f2617d99`
passed full hosted CI, including documentation publication, in
[34182926560](https://github.com/Furroxide/TopiaForge/actions/runs/34182926560),
and all four CodeQL analyses in
[34182924300](https://github.com/Furroxide/TopiaForge/actions/runs/34182924300)
reported zero findings. A duplicate PR-policy run canceled during ready/edit
events left a failed required gate; the complete run
[34183004289](https://github.com/Furroxide/TopiaForge/actions/runs/34183004289)
was rerun normally and passed. No branch rule was bypassed. The canonical
brief's remaining pending-permission sentence is corrected in this documentation
follow-up. Exact follow-up CI and the normal prerequisite merge still precede
slice 8 implementation.


## Release-preparation review follow-up, 8 September

The documentation follow-up `6df74c465ab950d0ca945c24cc74912e467a3763` passed
full CI [34183972734](https://github.com/Furroxide/TopiaForge/actions/runs/34183972734)
and CodeQL [34183970216](https://github.com/Furroxide/TopiaForge/actions/runs/34183970216).
All four scans reported zero new results; the temporarily missing C# configuration
cleared when its analysis completed. The ordinary merge API then identified four
unresolved Copilot conversations. The earlier review snapshots at 03:22:07/09 UTC
preceded their 03:22:29/30 arrival by twenty seconds; the previous empty inventory
was a stale snapshot, not proof that the PR had no later feedback. No merge or
protection bypass occurred.

Three comments identified inconsistent payload-name grammar: the catalog/BOM
filename contracts permit `+`, while both detached schemas and both candidate
readers rejected it. The regression run recorded 55 passes and six failures before
the repair; all 61 focused cases pass after adding only literal `+` to the existing
ASCII classes. Length, collection, path and case-insensitive collision restrictions
remain intact. Top-level release-version/tag and catalog-version rules are unchanged;
this evidence establishes filename parity, not a new promise that every component
version with build metadata qualifies. Logs are `tf-pr114-payload-names-red.log` and
`tf-pr114-payload-names-green.log` in the local temporary directory.

The fourth comment proposed resolving output paths against the repository root.
The actual CLI and builder consistently consume these paths relative to the caller's
working directory; changing only the source guard would break that agreement. Three
new subprocess regressions confirm nested-CWD acceptance and reject same-name asset
or metadata files under the wrong root. All eleven source tests pass, with no
functional path change (`tf-pr114-metadata-cwd-regression.log`). The API comment now
states the existing semantics. These are passing regression protections, not claimed
pre-fix failures. Combined verification and a fresh feedback inventory precede push
and normal integration. Slice 8 and live acceptance remain pending.


Combined local verification for this review follow-up passed: **441 CLI tests
with four platform-specific skips**, full fatal-info analysis, formatting of all
168 CLI Dart files, and all **513 non-generated Dart files** within 500 lines.
After restoring normal NuGet assets following the relocatable-SDK fixture, the
release-surface script verified eleven SDK packages and all seven C# harnesses.
Logs are `tf-pr114-review-cli-full.log` and `tf-pr114-review-seven-harness.log`
in the local temporary directory. All 50 website tests, 524 JSON/YAML files,
126 Markdown files, the documentation catalog and required repository audits
passed. Fresh hosted CI and current review-thread resolution still precede merge.


## Final-slice re-cut and local evidence (8 September 2026)

The branch was cut from `31ff4d49` after the normal merge of PR #114. The final
scope grew when safe acceptance required measured OS identity, a guarded runtime
acknowledgement and release-script admission. It is now split into **8a retirement
and authored-world fixes**, followed by **8b isolated acceptance**. No 8b delivery
branch exists before 8a merges. Full prepared source is preserved in the private
worktree archive `slice8-complete-source-before-recut.zip`, verified SHA-256
`467d368a868c616716b0ab16abbe84f104f79ebd2ad5f62bcb1db26f22177d6b`.

These observations describe local source preparation, not a release or CI verdict:

- Dart retirement plus V6 multiplayer regressions recorded 5 passes and 7 failures
  before the fix. V4/V5 guidance and the V6-only multiplayer semantic bypass were
  reproduced; the expanded focused suite passed 44 tests. Logs:
  `tf-slice8-dart-retirement-{red,green}.log` in the private temporary directory.
- Actual CLI migration subprocess regressions recorded 6 passes and 13 failures
  before repair. The command now uses preservation planning and one atomic writer;
  twenty focused cases and the additional V3 dependency-form case passed. Review
  then corrected a weak invalid-stub assertion: it had called nonexistent `validate`
  and only checked a failing exit. It now invokes `check package` and requires an
  implementation diagnostic, preserving absent optional world requirements.
- Migration planner/writer tests cover original-index diagnostics, raw scalar and
  schema-URL refusal, preserved unknown values/property presence, explicitly invalid
  stubs, sharing-denied replacement, cross-process leases, stale snapshots and links.
  Initial preparation passed 146 domain and twenty writer/profile cases. The
  added legacy-ID matrix recorded fourteen passes and eighteen failures before
  repair; all 178 combined migration tests pass. Source IDs retain their 64-character
  contract while current V6 declaration boundaries stay unchanged. Formatting
  can change, and cooperative source checking is not an OS compare-and-swap claim.
- C# retirement and V6 conformance passed after captured RED. The fixture corpus
  now contains 363 cases: retired V4/V5 are separate, active common fields use V6,
  and isolated transport ID cases replace the eighteen obsolete intent cases.
  Nine required-compatibility-range cases passed the existing C# reader but failed
  Dart before its repair. The corresponding 651-case Dart suite passed afterward.
- Editor marker validation now uses one exact, root/inactive-inclusive match and
  fails before HDRP/export writes. Nine production-linked helper cases passed after
  four reproduced failures. Prefab instantiation preserves the exact authored name
  inside allocation ownership; five new cases failed before the fix and passed
  afterward, including literal authored `(Clone)` names and throwing initialization.
  Seven Unity EditMode tests exist but were not run: the pinned editor is absent.
- Before the delivery split, full domain/data checks passed 1,036/489 tests, with
  four expected platform skips in data. The historical Windows multiplayer failure
  did not recur: all ten cases also passed in a clean detached `31ff4d49` worktree,
  using the same Flutter-bundled Dart and short Windows PATH. This is a matching
  baseline recheck, not an exemption from future Windows testing.
- Flutter application/UI tests passed 75/3, both analyzers were clean, and the
  Windows debug launcher built. Repository audits and their self-tests passed,
  including fixture closure, README counts, residue, trademarks and asset licensing.
  These combined-tree results are supplemented by fresh 8a-scoped gates below.

Prepared 8b source has separate focused evidence: 33 CLI acceptance tests, 42 native
and data tests, 31 C# admission plus six staging/native checks, and 24 release-script
isolation tests passed. The mocked administrator/qualification aggregate passed.
This source is excluded from the current delivery and needs fresh integration
checks after 8a. Its remaining review work includes denied-plugin global effects,
ACK-write failure after consumption, and injected native identity/resume failures.
None of those tests or harmless child processes proves game behavior.

No game process, candidate build, tag or publication occurred. Real approval and
rotation records, an isolated Windows QA context, visual/native acceptance and the
pinned Unity editor remain unavailable. Unsigned RC1 authorization supplies none
of that evidence. The acceptance/admin guides change with 8b, when those commands
are integrated; the current manifest reference and retirement guides change in 8a.


## Slice 8a scoped verification

After the acceptance source was separated, full domain/data suites passed
**1,068/489 tests**; data has four expected platform skips. All three Dart package
analyzers are clean. The full CLI run recorded **461 passes, four platform skips
and two failures**, both obsolete expectations for the current-V6 no-op message.
Exact-byte, validation and exit-code behavior passed. The wording expectations
were corrected and the entire **78-test actual command harness** passed, including
the three versioned invalid-stub calls through `check package`. The separate live
usage regression also passed all three cases after reproducing its stale command.
Hosted CI must run the complete final CLI tree before integration.

Fresh Release compilation passed with zero warnings/errors and the release-surface
runner verified all eleven SDK packages and seven harnesses. The first attempts
caught an unbounded read in the new editor test fixture (fixed with bounded config
reads and streaming hashes) and the nested disposable baseline being counted by
source conventions. After verifying its clean state and retaining test logs, that
baseline worktree was removed normally; the complete gate passed. After the relocated SDK CLI fixture, a final forced restore and fresh build
again passed with zero warnings/errors; all eleven SDK packages and seven harnesses
passed on those rebuilt binaries (`tf-slice8a-final-{build,seven-harness}.log`).

The independently scoped Flutter run passed **75 application and three UI tests**,
clean analyzers, and a Windows debug build. Formatting verified 523 relevant Dart
files with no changes; all **528 non-generated tracked Dart files** meet the
500-line cap. Fixture closure passes for **363 cases**. README counts, residue,
trademark and asset-licence audits pass, as do their self-tests. Repository data
validation covers 532 JSON/YAML files; Markdown links cover 127 files.

Full local publication was attempted. All fifty website tests, content preparation,
Astro validation/build, DocFX and domain/data Dart reference generation passed.
The bundled Windows Dartdoc 9.0.4 crashed in `_stripDocImports` while precaching the
unchanged Flutter UI dependency graph (`RangeError ... 9089: 9202`), matching the
previous Windows failure. Search and final built-link publication gates therefore
did not run locally; no full publication pass is claimed. Full hosted publication
against this exact branch is required. Logs use the `tf-slice8a-` prefix in the
private runner's temporary directory.

The final guide sweep corrected the active first-party catalog to V6 and accurate
Worlds/Free Play/session ownership. Its fourteen source packages and thirteen
non-DevTool payloads were verified from manifests and packaging behavior. The older
June documentation proposal remains intact as explicitly historical evidence with
current authority links. These documentation edits provide no game verification.


## PR #115 review and first hosted checks

Initial head `7d8fbf3e56dc28d3feb281153303e94d91642202` failed
[CI 34191489810](https://github.com/Furroxide/TopiaForge/actions/runs/34191489810)
on C# formatting and Windows migration tests. Linux domain/data/CLI, Flutter,
repository audits and Unity source checks passed. Template and full publication
jobs were skipped because their C# prerequisite failed. The CodeQL analyses
completed, but five new Editor-fixture path alerts failed the result gate; a
completed analysis was not a clean result. No merge or alert dismissal occurred.

The Windows failure was a new migration defect, distinct from the old multiplayer
observation: hosted TEMP used a legitimate 8.3 path that the new ordinary-file
check rejected. Three real short-path regressions failed before canonicalization.
The repair rejects source links and linked ancestors before resolving one canonical
snapshot and lease identity, and canonicalizes the ordinary OS temporary root.
Fifteen focused tests now pass, including lease contention and actual link refusal.
The full Windows data suite passes **494 tests with four existing platform skips**;
its analyzer and formatting pass. The full log is retained privately at
`packages/launcher_data/.dart_tool/pr115-windows-data-full-final.log`.

Copilot found that a minimal profile with no remembered selection was surfaced as
unavailable. Five new cases failed before the fix (six preservation cases passed).
An empty legacy selection now means main menu; explicit legacy property values,
including null and empty objects, remain preserved for repair. Malformed explicit
current selections still fail. All fifteen focused cases and **1,079 domain tests**
pass, with clean analysis. The application again passes **75 Flutter tests**, clean
analysis and the Windows debug build. Real CLI migration subprocesses also pass
all four `migrate` and seventeen `migration` cases after the writer repair.

The Editor fixture now owns its paired directory under its disposable project,
checks containment and ordinary filesystem kinds, bounds traversal and byte reads,
fences configuration restoration against external changes, and refuses export
command-line overrides. A compile-only Unity/NUnit facade harness recorded one pass
and three failures before these guards, then **fifteen filesystem checks passed**.
The subsequent repository source audit caught three unbounded assertion reads;
those now use the same bounded reader. This harness establishes filesystem behavior
only. All **fourteen actual Unity EditMode cases remain unrun**, and CodeQL must
confirm the path repairs on the pushed revision.

The two C# test files now satisfy the exact whole-solution CI formatting command.
Fresh Release compilation passes with zero warnings/errors; the rebuilt release
runner passes all eleven SDK packages and seven C# harnesses
(`tf-pr115-seven-harness-final.log`). All 530 non-generated Dart files meet the
500-line cap and all 460 scoped Dart files pass formatting. Repository audits,
363-case fixture closure, 127-file Markdown links and documentation content checks
pass. Refreshed `origin/dev` remains `31ff4d49`. Fresh hosted CI, full publication, template checks and CodeQL remain required
before normal integration; this review follow-up provides no game evidence.


## Slice 8a integration and superseded-stack closure

The repaired head `0c4a306` passed full
[CI 34193577478](https://github.com/Furroxide/TopiaForge/actions/runs/34193577478)
and [CodeQL 34193574648](https://github.com/Furroxide/TopiaForge/actions/runs/34193574648).
This includes Windows data tests, the complete CLI suite, all seven template
lifecycles, full documentation publication and zero open CodeQL findings. All
review conversations were resolved before normal merge of #115 at `1fbd32d`.
The local Windows Dartdoc limitation remains recorded separately from this
successful Linux publication evidence.

After integration, the carried #105 guidance/naming comments were answered with
replacement evidence and resolved. PRs #102–105 were closed as superseded, then
only their exact archived branch tips were deleted with explicit remote leases.
No active worktree owned those local branches. Signed annotated source tags retain
each original tip and its review mapping:

| Historical PR | Deleted branch | Preserved signed archive tag | Source tip |
| --- | --- | --- | --- |
| #102 | `feat/manifest-v6-contract` | `archive/gamemode-v6/pr-102-20260908` | `5d0874029b191ddd2b2df4a41e07fb2fa925a232` |
| #103 | `feat/manifest-v6-flip` | `archive/gamemode-v6/pr-103-20260908` | `00889c3271087ae8f2083a21ed69f15f64ecbf1e` |
| #104 | `feat/launch-resolution` | `archive/gamemode-v6/pr-104-20260908` | `d314599f1cd1097fcf72718a4e255deadf577ddd` |
| #105 | `feat/retire-manifest-v5` | `archive/gamemode-v6/pr-105-20260908` | `f3de112647d40b3d21d2a6e229ad193838468c28` |

These are source archives, not release tags. Distinct open work in #68, #95 and
#99 was not classified as superseded. Their latest review inventory still requires
fresh current-dev CI for #68/#99 and repair of #95's incompatible TypeScript peer
selection; none supplies or waives candidate acceptance.

## Slice 8b implementation checkpoint

Only after #115 merged, `feat/isolated-release-acceptance` was created from
refreshed `1fbd32d`. Exactly 51 archived paths were restored after verifying the
archive hash and equality of every saved prerequisite file. All 8a review fixes
remain intact. New regression work addresses the prepared source's remaining
ownership and protocol findings; current commands are reflected in the acceptance
and administrator guides.

- A Unity-free initialization lifetime, used by the real plugin and linked into
  tests, reproduced five failures across six lifecycle cases before repair.
  Admission now precedes persistence/storage; denied teardown is inert. UI cleanup
  responsibility begins before mod callbacks and survives partial initialization;
  throwing cleanup is attempted once. All six behavioral cases pass.
- Eight staging/native checks and 31 admission checks pass. Added ACK-write failure
  and competing-ACK tests passed the existing implementation immediately: V4 stays
  consumed, private request and competing ACK bytes survive, temporary files drain,
  and existing manager state stays untouched. No automatic V4 restoration was added.
- Release cleanup fencing reproduced two failures (seven cases already passed):
  a linked output or a future path beneath a link could hide a contained provisioning
  record. Ordinary-ancestor checks now precede containment checks. All nine actual
  filesystem cases pass, including existing short-name/case behavior. The normal
  release-admin aggregate registers these regressions and passes alongside the
  24 isolation cases and mocked qualification/orchestration checks.

These are local source and harmless-process results, not acceptance of a game or
candidate. Full scoped verification and hosted CI are still in progress. Four real
non-game reviewer/rotation records, an existing isolated QA context, the pinned
Unity editor and actual visual/native game evidence remain pending. The unsigned
Windows RC1 authorization remains recorded and does not replace these requirements.


Further 8b review added strict ACK raw-path checks: fourteen invalid equivalent
spellings failed to reject before repair while the Windows case/slash control
passed. The final ACK and admission suite passes 46 cases. A real 8.3 fixture
reproduced one failure and one pass before canonicalizing only its created
synthetic root; production alias refusal remains intact.

The production session monitor now requires liveness of the captured original
receipt before confirming ACK plus Running. False and unknown liveness reproduced
incorrect success (three passes and four failures including probe controls);
all seven polling scenarios pass after repair. Missing ACK/Running or uncertain
exit produces no acceptance result. The broader six-file CLI acceptance suite
passes 42 tests. Its omitted-package fixture now uses the receipt of the archive
it actually packed, with an intentionally changed assembly proving that the old
receipt cannot satisfy the assertion. No production receipt check was relaxed.

Three actual harmless-child native tests cover identity-read failure, resume
failure and delegated success. They were initially green after introducing the
scoped native adapters; no pre-fix failure is claimed. All eighteen focused native
cases pass. They verify original-object termination and confirmed exit before
both original handles close, complete creation-buffer cleanup, no PID reopen,
and a responsive unrelated child using the same executable. These are Windows
process ownership checks, not evidence about Unity scene completion.

Independent review of release cleanup found an additional extended-namespace
alias bypass: two new cases failed while nine passed. Device/extended namespace
spellings are now refused before containment checks; all eleven path cases pass.
Short-name and normal case behavior remain covered. This extends the earlier
linked-directory repair without adding another native path implementation.

The full rebuilt seven-harness C# release gate passes after fixing one stale
source inventory assertion: it now follows explicit, reciprocally declared runner
parts while retaining the existing complete-matrix assertions. Full domain tests
pass 1,079 cases; Windows data passes 546 with four existing platform skips.
Flutter UI/application tests pass 3/75, analyzers are clean and the Windows debug
launcher builds. Fifty website tests and required repository/content audits pass.
The full CLI run and final hosted verification remain in progress at this checkpoint.


## Slice 8b final local verification

The complete final CLI suite passes **492 tests with four platform skips**,
including packaged SDK relocation and all seven template build/pack lifecycles.
Domain/data remain **1,079/546 passes**, with four data platform skips. All three
fatal-info Dart analyzers are clean, all **551 non-generated Dart files** meet the
500-line cap, and formatting verifies **545 scoped Dart files** without changes.
All 67 explicitly owned delivery paths were checked before staging.

After the CLI fixture finished, a forced normal NuGet restore repaired the temporary
SDK asset relocation. The exact whole-solution formatter caught spacing in four
restored admission sources; only owned C# files were formatted, then whole-solution
verification passed. A fresh Release build has zero warnings/errors and all eleven
SDK packages and seven no-argument harnesses pass on those rebuilt binaries. Logs:
`tf-slice8b-final-{restore,format,build,seven}.log` in the private temporary directory.

The final synthetic release-admin/qualification aggregate passes with its 24
isolation and eleven real filesystem path cases (`tf-slice8b-release-admin-final.log`).
The configured PowerShell analyzer and shell syntax checks pass. Required repository
audits and their self-tests, 363-case fixture closure, 532-file JSON/YAML parsing,
127-file Markdown links and content preparation pass. Fifty website tests pass.

Full local publication was attempted again using the pinned tools. Astro validation
and build, DocFX, and domain/data Dart references pass with no reference warnings.
Windows-bundled Dartdoc 9.0.4 again crashes in `_stripDocImports` for the unchanged
Flutter UI dependency graph (`RangeError ... 9089: 9202`). Search and final built-link
checks do not run after that failure. The same limitation is documented against
the earlier matching baseline; the immediately preceding #115 hosted publication
passed. Fresh full hosted publication for this slice remains mandatory. Log:
`tf-slice8b-publication.log`. No local full-publication pass or waiver is claimed.

Refreshed `origin/dev` remains `1fbd32d`. Normal protected CI and review still precede
integration. No real candidate, game/Editor acceptance, release tag or publication
has occurred; the separately recorded reviewer and isolated-host prerequisites
remain pending.


## PR #116 review and initial hosted checks

Initial head `1689db3114631755dde58a5732eed4adc2e6e492` passed full hosted
publication, C#, Linux domain/data/CLI, Flutter, all seven generated-template
lifecycles and repository audits in
[CI 34197044470](https://github.com/Furroxide/TopiaForge/actions/runs/34197044470).
Windows data failed eight tests (538 passed, four skipped), so the aggregate CI
failed. [CodeQL 34197041913](https://github.com/Furroxide/TopiaForge/actions/runs/34197041913)
passed all four analyses with zero open findings. The successful hosted Linux
publication remains separate from the recorded local Windows Dartdoc limitation.

Seven Windows failures came from negative ACK fixtures using `path.relative`
without an explicit base. With checkout and TEMP on different drives, the result
is still absolute, so the test submitted a valid path. An isolated test-process
wrapper using different local drives reproduced exactly eight passes and seven
failures. The fixture now uses an explicit same-drive base, asserts that the
negative operand is actually relative, and verifies its resolution separately.
The cross-drive rerun passes all fifteen ACK cases; the combined admission,
ACK and comparator suite passes 53. Production raw-path admission was not weakened.

Copilot's path review found unconditional lowercasing in equality, overlap and
persistent-data containment. These operations now use the path package's host
semantics consistently. Seven regressions were added before the fix; they passed
on Windows both before and after it. No local Linux failure is claimed: a matching
existing Flutter-bundled Linux runtime was unavailable. Fresh Linux CI must execute
the case-sensitive negative branches. The focused Windows acceptance suite passed
53 cases before the separate cross-drive fixture repair.


The eighth hosted failure exposed a harmless-child fixture publication race:
the PID marker existed before `writeAsString` supplied its bytes. A deterministic
real-child barrier paused after opening the output and reproduced the incorrect
visibility before the fix. The fixture now flushes a same-directory temporary
file and renames it into place for PID and sibling-response publication. All four
native fault/control/publication tests pass; original handle, zero-reopen,
allocation and unrelated-child assertions remain intact. No production native
code changed for this fixture repair.


A fresh read-only inventory on 8 September fully enumerated review threads for
#68, #99, #92, #95, #97, #96, #77 and #72: none remained unresolved. #68/#99 have
green older heads but need current-dev integration checks and contain distinct
work. #92's configuration is already on dev and awaits the normal main release
promotion. The dependency proposals still have concrete failures: TypeScript 7
peer resolution (#95), Roslyn 5.9 versus the pinned 5.6 compiler (#96/#97), workflow
conflicts and trust assertions (#77), and a mismatched Flutter lockfile (#72).
Their proposed versions are not fully present on dev; they are not blanket
supersession candidates and do not waive this release's checks.


The receipt review first reproduced two failures across three cases: a transported
copy reached native callbacks instead of the captured object, and successful
cleanup retained ownership. Follow-up two-launch tests then reproduced stale and
failed request IDs claiming a newer receipt. A disposal barrier also reproduced
lost cleanup ownership when repository disposal completed during native creation.
The expanded pre-fix run passed five cases and failed three (request-bound ACK
admission and disposal); these are separate from the original clone regression.

Acceptance now retains a request-to-original-receipt mapping at successful
creation. One guard validates it for both ACK reads and stop, requiring that exact
object to remain owned. Native callbacks receive only that object; mappings are
consumed after confirmed exit and retained for unknown liveness. Disposal closes
monitoring while preserving acceptance cleanup, including a child returned after
disposal. Fifteen focused ownership cases pass (eight new acceptance cases and
seven existing V4 receipt-authority cases), with clean analysis and formatting.
These regressions use public repository launch/stop paths, synthetic installations
and controlled receipt callbacks; they do not establish Unity behavior.


The complete follow-up Windows data suite passes **562 tests with four existing
platform skips**. Formatting verifies 547 scoped Dart files without changes; all
553 non-generated tracked/new Dart files meet the 500-line limit. Independent
review of the final path, request/receipt, disposal and fixture changes found no
additional actionable defects. The complete CLI suite passes **492 tests with four platform skips**, including
the relocated packaged SDK and all seven generated-template lifecycles. Both
changed-package analyzers are clean. After the fixture, forced normal NuGet
restore and exact whole-solution formatting pass. The fresh Release build has
zero warnings/errors and all eleven SDK packages plus seven rebuilt no-argument
harnesses pass. Required repository, fixture, README and documentation audits
pass. Logs use `tf-pr116-{data,cli,final}-*.log` in the private temporary directory.
Final Windows/Linux hosted CI completed before integration; the following
section records the exact runs and merge.


## Slice 8b integration and remaining release work

Final reviewed head `eda671d40b51cddff098f34437510be552c6e46e` passed all final
required checks, including full
[CI 34199629221](https://github.com/Furroxide/TopiaForge/actions/runs/34199629221)
and [CodeQL 34199625355](https://github.com/Furroxide/TopiaForge/actions/runs/34199625355).
Fresh Windows and Linux data tests, CLI, C#, Flutter, seven generated templates
and complete documentation publication pass. All four CodeQL analyses pass with
zero open findings. Both review threads were answered and resolved. Updating the
final PR description briefly required a fresh policy check; it passed before
normal merge. No protection was bypassed.

[PR #116](https://github.com/Furroxide/TopiaForge/pull/116) merged at
`0182e19f166383685210151f07948e9909977b03` on 2026-09-08 at 07:42:38 UTC. The merge
tree equals the reviewed source tree. This checkpoint was cut from refreshed dev
only after that merge. It changes documentation only and records source readiness,
not acceptance of a release candidate.

The current release decision still blocks on actual IP, OSS, privacy and
credential-rotation records (`P0-IP-01`, `P0-OSS-01`, `P0-PRIV-01`, `P0-CRED-01`).
The game gate remains pending. The unsigned RC1 decision is already authorized;
Ed25519 signatures, exact payload hashes, provenance, detached qualification and
protected publication remain required. Normal release promotion and a frozen
main merge SHA precede candidate construction. No candidate, game acceptance,
release tag or publication has occurred.

The exact Unity Editor installer `6000.0.23f1` / `1c4764c07fb4` was downloaded from
Unity's official release endpoint into the ignored tool cache: 4,033,958,912 bytes,
SHA-256 `6e9f5f189079460cad6603ef5a5a8a3919fd6fe178cc579784ec788155c8a3c3`, with
valid Authenticode from Unity Technologies SF. Its explicit license agreement and
possible file-association changes require the requested setup authorization;
setup has not run. Other installed Editor versions and license-file presence do
not establish this pinned test result or valid headless entitlement.

A disposable project contains all 81 tracked UnityWorldTemplate files byte-for-byte
from `eda671d`, with a private input-hash inventory and the exact production
companion test assembly selected. Fourteen real NUnit cases are ready to run after
approved setup and successful existing-license admission. No facade substitutes
for these cases; no Unity XML or Editor result is claimed. An existing isolated
Windows QA context and the full visual/native game matrix also remain pending.
Keep raw paths, identity details, Editor logs and eventual private acknowledgement
records outside public release assets.


Checkpoint validation passes fifty website tests, 127-file Markdown links,
26-page content preparation, 532-file repository data parsing, README counts,
residue, trademark, asset-licence coverage and 363-case fixture closure. Runtime,
Dart and Flutter source trees are unchanged from the fully verified #116 head;
no new runtime or game result is inferred from this documentation-only update.


## Supplementary Editor diagnostic and RC host review

After [PR #117](https://github.com/Furroxide/TopiaForge/pull/117) merged,
the installed, signed Unity Editor `6000.0.31f1_a206c360e2a8` executed all fourteen
`WorldValidatorTests` cases through actual Unity/NUnit in a separate disposable
project copied from `eda671d`. All fourteen passed, none failed or skipped, and
the original Editor process exited with code zero. Existing license admission
succeeded without activation changes. This is supplementary diagnostic evidence:
`6000.0.23f1` remains the required authoring Editor, and its prepared project and
all 81 hashed input files were left untouched. Neither the sixteen authoring
cycles nor the game acceptance matrix ran.

The test source SHA-256 is
`2262b1e0fb8d71ae1205d9cd1462c368772510e2cb695710474d09da3d2defb8`.
Retained private XML has SHA-256
`91513204cb10d95c5813cd4cfb54c91b495e0e9810f116e57f45a57a0fb0a9fb`;
the Editor log has SHA-256
`7f668cf84e15658d7f822dc38cc10f788f364fcb6f6f7131eb1467e3feebbb80`.
Only the disposable project's version and VFX settings changed; test C# and
assembly definitions did not. Raw logs and machine paths remain private.

The subsequent read-only host review found no existing QA context in the bounded
locations examined. This does not rule out a maintainer-provided environment
elsewhere. The provisioning record must name an actual separately initialized
Windows user/session or VM. For RC1 its `outputRoot` must equal
`<StateRoot>/0.1.0-rc.1/evidence/windows/robotopia`, with the record outside the
output directories. Use an explicit external `StateRoot`; a checkout inside the
normal user's profile cannot also provide the isolated acceptance output root.
Release preflight and later publication can retain the operator's GitHub session;
the frozen-state build/acceptance phase can run in the QA session without copying
that credential store. Provisioning and visual/input observation remain pending.

The protected release environment contains the update-signing secret name but
lacks `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN`; repository secret names were also checked.
No secret value was accessed. The dedicated repository-only read credential
specified in [AdminRelease](../../AdminRelease.md) must be configured before
publication. The four nongame review/rotation records are still missing, and the
reviewer dossier prepared privately does not approve any gate. No candidate, tag,
release dispatch or publication was created by these checks.


## RC release preflight follow-up

The follow-up starts at `5acead7c855e899f0331b042a6794f424f48a467`. Live GitHub
responses identify the repository as `Furroxide/TopiaForge` and the same pinned
administrator with a capitalized login. Four shell paths rejected this valid
spelling before their fixes: governance, candidate provenance, asset fetching
and publication. Separate failing fixture runs precede each repair. The
PowerShell stage/dispatch regressions then independently reproduced rejection
of canonical governance repository, workflow uploader login and finalizer
repository names. All three failures preceded their corresponding fixes.

Only ASCII GitHub owner/repository/login spelling is compared without case.
Numeric principal/App/workflow IDs, principal type, repository identity, URL
host and path suffix, workflow path, refs, source SHA and request correlation
remain enforced. Different repositories/principals, Unicode lookalikes and
padded identities still fail. All three focused shell suites, their seven
syntax checks, the complete PowerShell release-admin suite and configured
PowerShell analyzer pass. The repaired shell and full Python live governance
audits also pass against the actual repository. This did not configure the
missing protected audit credential or exercise real publication.

The RC1 inventory was independently reviewed: twelve component versions,
thirteen shipped V6 mods, excluded UiGallery, two embedded VPM packages, and
exactly fourteen public payloads (Windows archive plus thirteen mods). Free Play
is owned by Worlds. Strict `release validate-policy --version 0.1.0-rc.1`, with
provenance hashes enabled, initially reported only the blocked catalog status;
it now passes after the inventory was marked `ready`. Forty-one focused catalog,
metadata, qualification and prerequisite tests pass. This approves inventory
only. The readiness register is unchanged, with SHA-256
`5b41655fd38a28c814ac0c7228cb953e379711d26501ba903fd496ee730b605d`, and retains
all four nongame blockers plus pending game acceptance. No private candidate
construction is admitted by the inventory change alone.

RoboAPI coverage now includes twenty-three actual loopback HTTPS cases for both
brain and speech operations: successful responses, HTTP 401 token reload,
429/500/503 failures, five redirect statuses without destination connections,
and default-transport certificate rejection with a positive control on the same
server. Synthetic token/session/audio values are used, and diagnostic assertions
retain redaction. An internal caller-owned transport boundary leaves the existing
three-argument production constructor, shared timeout, normal TLS trust and
redirect policy unchanged. Tests use a per-instance exact synthetic certificate
pin. No trust-store entry, global callback or live backend is used. Windows
Schannel required a temporary test certificate key container, disposed by its
owner; the initial ephemeral-key fixture failures are recorded as fixture
failures, not product defects. The full no-argument manager harness includes
these cases; microphone, privacy approval and native game evidence remain open.

Whole-solution formatting passes. A fresh Release build has zero warnings and
errors, and all eleven SDK packages plus seven rebuilt no-argument C# harnesses
pass. The official managed-reference latest-build probe still identifies game
build 2409. Operator examples now pass explicit shared source-game and external
state paths across sessions; release notes correctly describe integrated V5
retirement and intentionally invalid migration stubs. Documenting the actual
Windows game-evidence directory exposed an overly narrow residue allowlist:
a failing regression preceded its exact-directory exception. All 37 residue
regressions pass, including surrounding retired-name and unrelated-path refusals.
These source checks precede fresh PR review/CI; no candidate, tag or publication
result is claimed.

Final local documentation verification passes fifty website tests, all 127-file
Markdown links and 26-page content preparation. README, residue, trademark, asset
licensing and 363-case fixture audits pass. All 553 non-generated Dart files meet
the 500-line cap; formatting checks 483 scoped files with zero changes, and
domain/data/CLI analyzers report no issues. A local report wrapper first failed
printing a Unicode test-status symbol under Windows' default console encoding;
its UTF-8 rerun completed successfully. This was not a website test failure.


## Draft RC promotion and release-specific stabilization

[PR #118](https://github.com/Furroxide/TopiaForge/pull/118) passed the complete
hosted source matrix, including all seven generated templates and documentation
publication, before normal protected integration. The resulting `c0be924` tree
is identical to reviewed `54c9722`. After fetching that merge,
`release/0.1.0-rc.1` was created through the configured administrator-only
reference-creation allowance, and [draft PR #119](https://github.com/Furroxide/TopiaForge/pull/119)
was opened into `main` with auto-merge off. No update/merge checks or protected
publication approval were bypassed. The branch is review preparation, not a
frozen main candidate, tag or production build.

The fresh release-head [dry run 34206842067](https://github.com/Furroxide/TopiaForge/actions/runs/34206842067)
failed before ecosystem construction: the seven C# harnesses ran before the
locked CLI dependency restore required by the production generated-gamemode
binding test. The ModRuntime harness stopped with its actionable restore error.
Input verification, launcher builds and macOS x64 CLI completed; ecosystem and
platform packaging did not. These hosted dry-run outputs are not candidate
acceptance or evidence extending RC1's Windows-only distribution scope.

A workflow regression reproduced the missing prerequisite ordering before the
fix. The canonical ecosystem job now restores `apps/topiaforge_cli` with pinned
Dart and `pub get --enforce-lockfile` before any C# release harness. All six
workflow source-trust tests pass, and all 532 repository JSON/YAML files parse.
The redundant later restore is removed; every SDK/harness and deterministic
payload check remains required. A fresh actual release-head dry run must prove
the repaired cold-job sequence after stabilization merges.

The main-targeted CodeQL check also reported ten high path-injection findings
(alerts 431 and 463-471), although the narrower PR #118 comparison had reported
none. Exact SARIF traces all ten to two test-runner temporary roots allocated
with `GetTempPath`, a generated suffix and non-exclusive `CreateDirectory`;
eight downstream locations are fixed test fixtures, not manifest-controlled
paths. Those two entries now use atomic owned temporary-directory allocation,
matching the existing full-run convention. No finding is dismissed or
suppressed, and production reader/validator paths remain unchanged. The exact
failing security check is the recorded pre-fix evidence; no exploit or failing
production validator is claimed. Hosted CodeQL must re-evaluate the repaired
release head before promotion.

A separate behavioral probe found cleanup failures despite successful test exits:
`--sdk-lifecycle`, `--session-lifecycle` and the full manager run left 6, 30 and
40 sibling fixture directories respectively. Their call sites now give each
suite a child of the runner-owned root, including suites that append their own
suffix. The same probes then left zero siblings and retained an unrelated
sentinel in all three modes. No historical temporary directory was removed.

`HarnessWorkspaceTests` makes this regression reusable in the seven-harness
verification path. It launches three existing focused modes in independently
owned temporary parents, checks their exit codes and the retained sentinel,
and rejects leftover fixtures. The committed test failed against the old SDK
and session call sites before the fixed calls were restored. It runs in the
normal no-argument manager suite as well as `--harness-workspaces`; its children
return before that registration and cannot recursively launch the full suite.
Child execution, termination and output drain are bounded. Independent review
also moved cleanup admission after successful process retirement and output
drain, so a failed drain preserves the private parent for diagnosis.

Final local stabilization verification rebuilt the full Release solution with
zero warnings/errors, then rebuilt/audited all eleven SDK packages and ran all
seven no-argument C# harnesses successfully. Whole-solution formatting returned
zero with the existing workspace-load warning. The workflow regression suite,
README counts, residue, trademark, asset-licence and 363-case fixture closure
checks pass. Pinned Dart formatting reports 489 files with no changes;
domain/data/CLI analyzers report no issues, and every tracked non-generated Dart
file remains at or below 500 lines. These local checks do not substitute for
fresh hosted CodeQL, cold release packaging or exact-candidate game acceptance.

The active governance, architecture inventory, administrator guide, blocker
runbook and update guide are aligned with the recorded Windows-only, unsigned
RC1 policy. They distinguish its fourteen catalog payloads, eighteen human-staged
assets and five finalizer-generated metadata assets from future platform or
signing requirements. Qualification, exact-byte staging, Ed25519 update
signatures and protected approval remain required. Pages source rules now match
the implemented trusted-main/stable-tag workflow. No policy, readiness row,
credential or repository protection changed in this documentation correction.

## Release-head verification and neutral build roots

PR #120 squash-merged normally at `7dbf08f35141780830cfb88a8f7046d718093bcf`
on 8 September after full CI 34209930441 and CodeQL 34209928506 passed on
reviewed `fad5129`. The verified signed merge includes the required sign-off,
and its tree is identical to that reviewed head. Updating the PR description
triggered a fresh policy check; the first merge attempt waited for that required
check, then normal protected merge succeeded. No protection was bypassed.

On the resulting release head, main-targeted CI 34210949124 passed, including
Windows data, all generated templates and full documentation publication.
CodeQL 34210944179 passed and the broader PR #119 comparison has no open
findings. The earlier ten path findings therefore cleared on the actual release
comparison as well as the narrower stabilization PR.

[Release dry run 34210945161](https://github.com/Furroxide/TopiaForge/actions/runs/34210945161)
then passed the repaired CLI restore, all eleven SDK audits and seven harnesses,
both independent ecosystem builds, byte-identical comparison and residue scan.
Its Windows package completed smoke tests, signed-update/forced-rollback tests,
all seven packaged template lifecycles and artifact upload. These are hosted
synthetic checks, not private candidate qualification or game evidence.

The complete dry run still failed: Linux and macOS packages built successfully,
but package smoke found `/home/runner/` in the Linux CLI and `/Users/runner/`
in the macOS arm64 CLI. A completed job is not necessarily a successful one;
build success must remain distinct from package validation. Those compilations
used the runner's personal checkout and package cache. The package validator
correctly rejected the embedded home paths and remains unchanged.

The follow-up workflow repair compiles shipped CLIs from a fresh physical
neutral source copy, a relocated pinned Dart SDK and a new locked package cache,
then supplies those binaries through the existing prebuilt-CLI interface.
Flutter builds use the same neutral source/SDK/cache boundary, including native
plugin sources, macOS inspection and final archive construction. Hydrated
tracked source is copied; generated package configurations are restored at the
new location. Pinned SDK archive verification and source admission remain
required. Local source-wiring regressions failed before these changes; the
fresh hosted binary scans must validate every resulting artifact after merge.

This hosted repair does not certify the administrator's future local source,
SDK or cache locations. Production builds must also use neutral physical roots
and pass the same unchanged binary scanner. The four non-game approvals,
pinned-Editor execution, isolated QA provisioning and exact-candidate acceptance
remain pending. PR #119 remains draft, with auto-merge off; no tag, candidate
qualification or publication occurred.

The neutral-root helper has thirteen passing behavioral cases on native Windows
Git Bash and thirteen on Linux through WSL, including hydrated tracked bytes,
paths with spaces, dirty/index refusals, personal-root rejection and cleanup
identity checks. The replacement symlink test uses only an owned synthetic
sentinel with native-strict link creation. The helper never reassigns the real
home variables. macOS/BSD tool execution remains for the hosted runner.

Source-wiring checks pass after reproducing the original missing neutral roots
and the Windows drive-letter/PATH conversion defect. The optional Flutter
installation input preserves the existing default path and verified archive
flow. A final Release rebuild again reports zero warnings/errors and all eleven
SDK audits/seven harnesses pass. Repository, fixture, documentation and pinned
Dart checks pass; fresh hosted packaging remains necessary to prove clean binary
bytes, and no production acceptance claim is made.

## Package validation follow-up on the verified release head

PR #121 squash-merged at `0254e62506627a9c1677dd6926bee38525d35a41`
with a valid GitHub signature and sign-off; its tree matches reviewed `ebf5a15`.
Both CodeQL runs and the PR #119 security gate passed, with no open findings
on that comparison. Release-push CI 34215248200 passed, including publication.
Main-targeted CI 34215253689 passed publication but failed one Windows data
test: seven missing-DLL scenarios and an initial install shared one thirty-second
timeout. The timeout started teardown while the asynchronous body still ran.
The same source and runner image passed that case in the companion push run.

The seven scenarios now have independently named tests, fresh fixtures and
captured repository/path references before their first await. Assertions and
the default timeout are unchanged. The recorded hosted failure is the regression
baseline; nine focused tests pass after the change. The complete local Windows
data suite passes 568 tests with four platform skips when Windows PowerShell
is reachable on an explicit toolchain PATH. An initial run with the inherited
PATH reproduced seven multiplayer-pack failures, each reporting that
`powershell.exe` could not be found; those are not accepted as a permanent test
exemption.

[Dry run 34215249224](https://github.com/Furroxide/TopiaForge/actions/runs/34215249224)
completed with full Windows and Linux package success: smoke, signed-update
rollback, all seven packaged template lifecycles, residue and artifact upload.
All three neutral Flutter builds, CLI builds, eleven SDK audits, seven harnesses
and byte-identical ecosystem checks passed. The overall run failed because
macOS smoke found `/Users/runner/` in `TopiaForge.GameCompat.Extractor`.
Recursive scanner order does not prove that every other macOS binary passed.

Inspection of the official pinned `Microsoft.NETCore.App.Host.osx-arm64` and
`osx-x64` 10.0.9 packages established that their unmodified `singlefilehost`
binaries already contain these public Microsoft runtime source paths: nineteen
arm64 occurrences and fourteen x64 occurrences, representing twenty-one distinct
ASCII strings. Their `apphost` counterparts contain none. Source `PathMap`
cannot rewrite these already-compiled native bytes; disabling signing or allowing
an entire home prefix would be the wrong repair. The checked-in provenance
inventory records package/member SHA-256, architecture, byte offsets and the
upstream source revision. These hashes establish the literals' public origin;
they do not authenticate a whole final modified or bundled executable.

The scanner correction recognizes only those exact, NUL-bounded ASCII source
literals. It continues checking all other bytes, including unknown paths with
the same prefix, longer suffixes, adjacent private paths and unobserved UTF-16
forms. Boundary tests reproduce the false positives before the repair; bounded
lookahead prevents a split approved literal from being rejected prematurely.
No account, path-prefix or whole-binary exception is introduced. Hosted package
validation must still pass on the replacement release head before promotion.
Readiness approvals, pinned Unity execution, isolated native acceptance and
exact-candidate qualification remain pending.

A clean archive of release head `0254` reproduces ten passing multiplayer
tests with that same PATH and pinned Dart. All 320 archived source files were
verified against their Git blobs before and after execution. Because this data
package does not track its lockfile, the passing checkout's matching lock was
separately hashed and restored with enforcement; it is recorded as a test
input, not as tracked baseline source. The earlier seven-test Windows exception
is therefore obsolete. A fresh Release build has zero warnings/errors and all
eleven SDK audits and seven no-argument C# harnesses pass again.

Final local package-validation checks pass: 41 scanner/provenance cases,
75 package-builder cases, and direct scanning of all four original upstream
macOS apphost/singlefilehost members. Pinned Dart formatting changes no files;
domain/data/CLI fatal-info analyzers are clean and all 555 non-generated Dart
files stay within 500 lines. README, residue, trademark, asset licensing,
363-case fixture closure, 533 JSON/YAML files and documentation content/links
pass. Actual compiled-CLI and fresh hosted verification remain separate below.

## Optional diagnostic discovery after successful package validation

PR #122 squash-merged normally at `c76cfd6a00d946b9302e8c638eb5dc69e1feadfd`
on 8 September at 11:13:25 UTC. The signature/sign-off are valid and its tree
matches reviewed `6179d6d`. CI 34218468036 and CodeQL 34218468336 passed on
that reviewed source. The sole review comment overlooked the immutability
test's guarded cleanup; the recorded red run demonstrated the intended matcher
failure without a cleanup exception, and the evidence reply resolved it.

[Release dry run 34219552876](https://github.com/Furroxide/TopiaForge/actions/runs/34219552876)
passed all eleven jobs on `c76cfd6`: all three platform package smoke tests,
signed-update and forced-rollback tests, every packaged template lifecycle,
residue checks and uploads. Neutral launcher/CLI builds, eleven SDK audits,
seven C# harnesses and the exact ecosystem comparison also passed. These are
hosted synthetic artifacts; no administrator candidate was built or qualified.

The same-head release-push CI 34219552329 passed, but main-targeted CI
34219557490 exposed a different Windows defect: optional `where "Unity Hub.exe"`
exceeded its existing five-second bound and its exception aborted the entire
environment report. The failed test was supposed to retain an actionable,
blocking missing-.NET SDK result. All seven previously split DLL cases passed.
GitHub's tested main merge `ffa38b6` and the release head have identical trees,
so this difference did not come from extra main-branch source. The exact results
were 567 passes, one failure and four skips versus 568 passes and four skips.

Optional discovery is now isolated at the diagnostic report boundary. Unity
Hub, Editor and Git failures become explicit availability-unknown warnings;
each remaining probe still runs and a known matching Editor stays usable when
Hub discovery fails. The doctor retains its existing Hub/Editor scope. Both
reports preserve blocking required-SDK failures, and confirmed empty lookups
still mean not detected. Process/exception details are sanitized in warnings.
The five-second and output-size bounds are unchanged. Required editor discovery
and world-build callers retain their original error behavior, and programming
errors are not silently converted to optional-tool warnings.

The typed lookup seam precedes any configured Hub override, so regressions do
not depend on a user's ambient Unity installation. The original missing-SDK test
also supplies lookup/editor fixtures rather than spawning unrelated real probes.
Thirteen deterministic cases failed before the report repair; sixteen new
regression/control cases and thirty-four combined focused cases pass afterward.
The matrix covers timeout/spawn failure, independent and combined outcomes,
unknown versus absent, a usable SDK, a matching Editor, sanitized diagnostics,
and preservation of non-report and programming failures. Independent review
found no actionable issue.

The final two validator-DLL scenarios also receive individual captured fixtures
and unchanged default timeouts, matching the seven module cases. Their prior
three-transaction case passed the slow runner in approximately seventeen
seconds; it did not cause this failure. Auditing the other runtime-repair tests
found no further batch of independent transactional repairs inside one test.
All ten focused payload tests pass after the split. Candidate readiness rows,
Unity authorization, isolated QA and actual game acceptance remain pending.

Final local verification passes with 585 Windows data tests and four platform
skips, plus the targeted CLI doctor case. An initial overlapping run recorded
584 passes, four skips and a missing analyzer DLL while the C# SDK audit was
rewriting that output; the retained log and file timestamps identify the
coordination error. The complete data suite then passed after all build writers
finished. No source, assertion or timeout was changed between those runs.

The freshly rebuilt Release solution has zero warnings/errors and all eleven
SDK audits and seven no-argument harnesses pass. Formatting changes none of
464 checked Dart files; all three fatal-info analyzers are clean and all 556
non-generated Dart files remain within 500 lines. README, residue, trademark,
asset licensing, 363-case fixture closure, 533 JSON/YAML files, documentation
content and 127 Markdown link checks pass. Hosted verification of the committed
fix and subsequent release-head packaging remain separate requirements.

## Administrator neutral-build instructions correction — 8 September 2026

Read-only tracing at `27b5b962b6006570522c657ca134b28f36d1f831` confirmed that
private Windows candidate construction selects the checkout's FVM SDK before
`PATH`, inherits `PUB_CACHE`, and checks SDK versions before building. It does
not relocate personal SDK/cache roots; `release test-package` remains the final
embedded-path enforcement boundary. `AdminRelease.md` now requires physical
neutral source, exact-SHA worktree, fresh pinned SDK and pub-cache locations,
with locked restores and no copied personal caches or profile/credential import.
The obsolete `subst` recipe is removed. The hosted neutral helper omits `.git`
and cannot replace the administrator's final-main source-provenance checkout.

This is an operator-instruction correction only. No build scripts, scanner
allowances, release policy, gate decisions or qualification evidence changed.
No private candidate build or game acceptance was performed; the actual Unity,
isolated QA and human review prerequisites remain pending.

The same operator audit found obsolete executable WSL2/Proton instructions and
claims that a policy change would restore the retired runner. The administrator,
live acceptance, release operations, governance and blocker guides now state the
actual refusal and the need for reviewed native isolation and candidate evidence
before future platform approval. Historical setup and descriptor commands are
removed from the active runbook; prior graphics evidence remains historical, and
no future release version is promised.

PR #123 merged normally at `b1d98904636f0f96e15e6678817c935cd300b705`,
with a valid signature/sign-off and the reviewed source tree preserved. Its CI
and review completed before this separate operator-documentation correction.
That merge does not qualify a private candidate or supply game evidence.
