# RC1 prerelease merge and publication handoff

Updated 2026-09-24. Target: `release/0.1.0-rc.1` into `main`, then
`v0.1.0-rc.1` as an early prerelease of 0.1.0. **Not qualified for publication.**
This handoff records preparation; it supplies no reviewer approval or candidate
acceptance. The [release checklist](../ReleaseChecklist.md),
[administrator runbook](../AdminRelease.md), and
[tracked readiness register](../../release/release-readiness.json) remain authoritative.

## Scope and disclosures

The existing catalog declares `prerelease: true`. RC1 distributes one Windows
x64 archive for Robotopia build 2478 and thirteen first-party mod packages. The
archive embeds two VPM packages. The build moved from 2409 on 2026-09-24 under
the kept `requireLatestAtRelease` policy; see [the launch evidence](launch/Evidence.md#build-2478-retarget-2026-09-24). Linux and macOS are outside this release; no
future platform release date or version is promised.

Windows executables are intentionally unsigned. This does not waive checksums,
Ed25519 update signatures, exact-byte verification, or protected publication.
The [release notes](../../release/notes/v0.1.0-rc.1.md) describe the public
experimental scope and limitations. A disclaimer does not establish permission
to redistribute assets or evidence of a successful test.

## Before the final main merge

- Integrate release stabilization through ordinary same-repository PRs into
  `release/0.1.0-rc.1`. Preserve unrelated local work and do not infer that an
  uncommitted implementation belongs in the frozen candidate.
- Preserve the dependency repairs integrated through
  [PR #129](https://github.com/Furroxide/TopiaForge/pull/129): Astro 7.2.8,
  Sharp 0.35.4, smol-toml 1.8.0 and SVGO 4.1.0. Require fresh passing dependency
  review on the final release head, alongside all other required checks and CodeQL.
- Obtain actual attributable approvals for `P0-IP-01`, `P0-OSS-01`,
  `P0-PRIV-01`, and `P0-CRED-01`, and integrate their valid safe references.
  Their tracked records are blocked with no evidence IDs. Only `P0-GAME-01`
  may await the final candidate. Credential closure was previously deferred;
  release intent does not supply closure evidence.
- Review and decide the game-QA policy PR. Under the user's 2026-09-24
  decision it makes `P0-GAME-01` advisory with an owner disposition, gives
  `P1-UX-01` and `P1-E2E-01` accepted-risk dispositions, and adds a frozen
  `-LiveGameAcceptance run|not-run` build mode with a truthful not-run
  acceptance record. The sixteen Unity authoring cycles stay mandatory. It
  takes effect only when the user merges it.
- Preserve all advisory rows and record actual dated owner dispositions where
  evidence remains incomplete. Keep private review records, raw logs and
  credentials outside Git and public release assets.
- Configure the dedicated protected `release` environment secret
  `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` through secure entry. It was absent in the
  2026-09-10 name-only check. Follow the repository-scoped read-only permissions
  in [RepositoryGovernance](../RepositoryGovernance.md#trusted-release-and-deployment-environments).
  The update-signing secret exists; existence does not prove recovery or validity.
- Complete reviewed QA admission, neutral build preparation, and signing-key
  recovery prerequisites under their actual authorizations. Existing source
  tests, development QA provisioning and synthetic package runs cannot replace
  those records or final candidate acceptance.
- Refresh [PR #119](https://github.com/Furroxide/TopiaForge/pull/119) with the
  final release head and results. Disable draft only when source preparation
  is complete. Leave auto-merge off; the user performs the final merge.

At the initial 2026-09-10 inspection of `6d4082d`, dependency review was
PR #119's sole failing required check. PR #129 repaired that failure. At
`f0ef217`, all required checks and CodeQL passed; PR #119 remained a draft.
No RC1 tag or GitHub release existed. Later release heads need fresh checks.

## Integrated stabilization evidence

PR #129 was squash-merged normally at
`f0ef217485bb4519e202387c2f68757316d746f2` after all required checks and CodeQL
passed on reviewed head `7859046ca6cef60d49955b06bee01363fd406e7e`.
[Full CI](https://github.com/Furroxide/TopiaForge/actions/runs/34418453127)
passed all 16 jobs, including seven templates and the complete guide, C# API,
Dart API and search build. Local website validation passed 50 tests and reported
zero npm audit vulnerabilities. On integrated release head `f0ef217`,
[main-target CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419132880)
and [release-push CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419131141)
each passed all 16 jobs; both CodeQL runs passed. The
[packaging dry run](https://github.com/Furroxide/TopiaForge/actions/runs/34419133863)
passed all 11 jobs. Later source changes require fresh checks. None of these
source or synthetic package results qualifies the production candidate.

The subsequent cleanup repair propagates production creator-source disposal
failures through stop, removal, undo, graph actions and session teardown. It
continues unrelated owned cleanup, preserves startup and rollback errors, and
prevents retired callbacks from restoring undo history. Production-backed
regressions cover eleven cleanup routes plus session retirement and startup
rollback. Generated runtime acceptance fixtures use their explicit local SDK
feed without inheriting unrelated host feeds; failures retain subprocess output
and the existing deadlines. Local full Release build, manager and runtime
harnesses pass. [PR #130](https://github.com/Furroxide/TopiaForge/pull/130)
passed all 16 CI jobs, CodeQL and required checks on reviewed head `ec03955`,
then was normally squash-merged with explicit user authorization at
`0ea59e7941b1d56f515ac34f89819f31f16c7137`. Its tree is exactly the reviewed
`befbabaeaed9bdc55d4b14185af4c49804afe65c`. On this exact integrated head, both CI runs passed all 16 jobs, both CodeQL
runs passed and the packaging dry run passed all 11 jobs. PR #119 has been
updated with the exact source, results and remaining prerequisites. It remains
a draft with auto-merge off and no unresolved review threads.

The timeout diagnostic follow-up shares one five-second cleanup budget for
process exit and stream collection. Controlled ordinary and descendant-held-pipe
timeouts returned in 2.060 and 7.014 seconds, preserving the original failure.
The final local Runtime suite passed in 22.26 seconds, and the same helper in
the original development checkout passed in 24.58 seconds.

On 2026-09-24 the release branch integrated, each after all required checks
and CodeQL passed and under the user's explicit merge authorization:

- The build-2478 retarget ([PR #137](https://github.com/Furroxide/TopiaForge/pull/137)).
- Launcher guidance for a game newer than a mod supports
  ([PR #139](https://github.com/Furroxide/TopiaForge/pull/139)).
- The Sandbox automation and QA provisioning tooling
  ([PR #131](https://github.com/Furroxide/TopiaForge/pull/131)).
- Two native world-loading fixes
  ([PR #138](https://github.com/Furroxide/TopiaForge/pull/138)).

The [launch evidence](launch/Evidence.md#build-2478-retarget-2026-09-24) records
the audit and results. It also records what did not run: no game was launched,
and no native Sandbox row, provisioning retry or candidate acceptance ran.
Source tests and Editor results are not candidate acceptance. The user answered
the full-versus-reduced scope question; the resulting policy change is the
separate PR above.

## After the user merges

1. Verify the final `main` commit has two parents, its second parent is the
   checked release head, and its tree matches that head. Freeze that actual SHA
   in a clean checkout equal to `origin/main`.
2. Use the same explicit external state, verified source-game and reviewed QA
   record paths throughout `release-admin.ps1 preflight` and `build`. Preflight
   must report `eligible-for-private-build`; it does not grant SHIP approval.
3. Build and test the exact candidate bytes. Complete the sixteen Unity
   authoring cycles. The SDK cases, gamemode cases and isolated native game
   acceptance are required unless the game-QA policy PR has merged. After that
   merge they are optional, and a `not-run` build records that truthfully.
   Preserve failures and incomplete observations; do not substitute hosted
   dry-run artifacts or development fixtures for the candidate.
4. Obtain reviewed detached candidate readiness and acceptance records and run
   `release-admin.ps1 qualify`. It must record `accepted`. Freeze the payloads;
   changes require a new build and acceptance.
5. Obtain the recorded final SHIP decision. Run `stage` to create the signed
   annotated tag and matching draft with the 18 approved human-owned assets.
   Tagging occurs here, after qualification.
6. Run `dispatch`, then obtain the protected `release` environment approval.
   The finalizer verifies and publishes the exact prerelease, adding five
   metadata assets for 23 public assets. Keep the prerelease flag true and
   exclude it from stable discovery feeds.
7. Verify the immutable release and assets, published version, checksums,
   Ed25519 metadata and verifier attestation. Retain the publication receipt.

The final-main candidate does not exist before the merge. Successful source
promotion therefore cannot promise immediate publication: candidate construction,
acceptance, qualification and protected approval still follow in that order.

## Current local launch status

This local handoff supersedes the historical pre-integration observations in
the release branch's handoff. Integrate its final status alongside the eventual
reviewed gate/scope changes before the main freeze. [The launch hub](launch/README.md)
and [decision log](launch/Decisions.md) preserve the user's instructions.

The corrected QA retry ended at 22:13:23Z on 2026-09-09 before broker startup:
the active QA desktop did not become available within its nine-minute budget.
Its failed receipt is retained and the one-time task was removed. It supplies
no native verification of the shutdown correction or isolation admission.
No new native attempt was dispatched during this release stabilization.

The 2026-09-11 retry ran the game and reached Unity OnApplicationQuit, but again exceeded the 90-second deadline. Original-game exit was confirmed after forced cleanup; the task was removed. The diagnostic launch mode that retains the Unity log and read-only runtime snapshots is verified on a fixture player. Retry `20260911T183026Z` was staged and refusal-checked without dispatch. Its game copy is build 2409, so it no longer matches the pinned build. Verified 2478 copies were made on 2026-09-24 as the user authorized; an optional retry still needs a re-staged bundle before any operator session. See the [current observer runbook](launch/runtime-provisioning-observer.md).
