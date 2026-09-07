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
| `feat/gamemode-launcher-integration`, based on `bd7be8be` | Slice 7 active working tree | Shared selection, preview, wire and progress integration in progress; not integrated or game-verified |

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
| GM-10: declared authored spawn ignored | World instance returns resolved spawn; provider validates marker readiness; unsupported dead schema remains excluded | Missing/duplicate marker fails; correct marker resolves before StartAsync; templates use production binding | Authored-marker world starts at intended position; Open Sandbox and discovered providers report actual spawn | 2, 5, 6, 8 |

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
