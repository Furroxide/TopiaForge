# Slice 7a: Release preparation

Begin only after slice 7 merges into `dev`. Read the [canonical brief](../../GamemodeContractRedesign.md), [evidence ledger](../Status.md) and [common rules](README.md). This bounded prompt incorporates the 2026-09-06 review; it is not approval or acceptance evidence. Recheck source locations and remote state before implementation.

## Delivery placement

Implement this explicit release-preparation PR after slice 7 integrates and before slice 8 freezes its final acceptance candidate. Keep all eight redesign slices and their acceptance requirements. This is an additional release trust-contract change, not a silent widening of slice 8. Obtain exact-head CI against current dev and merge normally. Do not create later branches before their prerequisites merge.

## Protected promotion and workflow source

The 2026-09-06 remote check found default branch `main` at `f7d154a5bc5880b75f9f4a4bbaf9a34a5158c76e`, while `dev` was `9dc5613bc0cc8f2fa9b3c50b02357116f2540514`. Refresh these facts before release; development CI does not promote workflow definitions or certify the default branch.

- After the required redesign slices integrate, promote through a same-repository `release/0.1.0-rc.1` PR into `main`, matching `release/release-policy.json`. `.github/workflows/pr-policy.yml` rejects direct `dev -> main` promotion.
- Preserve `tools/verify-release-candidate.sh`: one exact merged release PR, a two-parent merge whose second parent is the checked release head, and identical merge/head trees. Squash/rebase promotion cannot satisfy this contract. Resolve promotion conflicts before obtaining final release-head checks.
- Obtain all six required hosted checks on that release head with their existing workflow/run/job provenance. `Required / Release packages` must come from a release-branch **push**, not a manually dispatched substitute.
- Freeze and test the final `main` merge SHA. `release-admin` requires a clean local `main` exactly matching `origin/main`; build/stage and protected verification recheck it. A release-head SHA is not the acceptance SHA even when trees match. Moving `main` invalidates the candidate; preserve this rule in the detached qualification repair.
- Dispatch `release.yml` only from the exact signed annotated version tag, using the journaled request ID. Preserve tag-only protected approval, governance checks and revalidation after approval and immediately before publication. Never run a stale/default-branch publisher or bypass promotion protections to finish acceptance.
- Pages accepts successful `main` push CI, explicit `main`, or a published immutable **stable** release tag. An RC prerelease tag does not satisfy its stable-tag gate. Verify the promoted workflow source before publication and keep this trust boundary intact.

## Actions security findings by ref

Recheck alert **instances**, not only `most_recent_instance`. On 2026-09-06 all three latest instances pointed to old `main`, but their per-ref states differed:

| Alert | Verified state and required follow-up |
| --- | --- |
| #409 | Open on current `dev` `9dc5613` and old `main`; `deploy-pages.yml:134-137` calls the transitive Flutter setup action. `.github/actions/setup-flutter/action.yml` is identical on both refs and still uses read/write `actions/cache`. Pinned archive digest validation is a mitigation, not closure of the reported cache writer. |
| #416 | Fixed on `dev` (recorded at `efa0ee53`), still open on old `main`. Carry the existing source repair through promotion and verify the resulting default-branch analysis. |
| #417 | Still open on current `dev` `9dc5613`, at `release-package-build.yml:196-203`, as well as old `main`. Do not infer closure from the managed-reference cache repair or a successful PR CodeQL run. |

Managed-reference caches in Pages and release-package-build are already restore-only on `dev` (changes carried by `a5d8677`, PR #66). Review the surviving #409/#417 flows, including transitive actions and post-job cache saves; add a bounded regression and repair the actual remaining source before promotion. Obtain fresh Actions-CodeQL evidence at the repaired head and promoted source. Preserve checked archive digests and trusted publication boundaries. Do not dismiss alerts, silently weaken analysis, or classify an open `dev` instance as stale-main-only. No alert was dismissed during this review.

## Confirmed ordering defect

- tools/release-admin.ps1:1054 invokes release validate-readiness during preflight.
- apps/topiaforge_cli/lib/src/release_readiness.dart:99 reads readiness and schema blobs at the exact candidate SHA. P0-GAME-01 must already be approved.
- Invoke-Build requires preflight state; tools/release-admin.ps1:2708 builds the Windows candidate and tools/release/build-windows.ps1:627 invokes acceptance afterward.
- -Rehearsal follows the same preflight through Invoke-All; there is no honest bootstrap path.
- Committing approval after testing changes the candidate SHA. Preallocating an evidence ID passes syntax validation but does not establish evidence or approval. --allow-unresolved-policy remains non-distributable.
- Build-Handoff runs after live acceptance and does not depend on readiness; its immutable digest is a suitable qualification anchor.
- Catalog status is another ordering concern: its ready value should approve the reviewed artifact inventory, not assert permission to ship. The final candidate decision remains authoritative. Correct existing prose tying catalog readiness to a fully approved tracked decision.

## Settled distribution and bounded repairs

The user selected an unsigned Windows x64 0.x prerelease, version 0.1.0-rc.1. Linux/Proton/macOS are outside RC1. Preserve signed defaults and the restriction permitting unsigned only for 0.x prereleases.

1. Set signingIdentities.windowsDistribution to unsigned in reviewed release policy, without a Windows certificate pin.
2. tools/release/build-windows.ps1:341-349 currently unconditionally requires certificate/password/timestamp credentials after branching on mode. Require these only for signed mode.
3. apps/topiaforge_cli/lib/src/release_package_builder.dart:131-141 always invokes WindowsPackageSigner.signIfConfigured. Explicit unsigned policy must skip Authenticode even when ambient signing environment variables exist. A contradictory --require-windows-signing must fail before writes.
4. Retain package validation proving all three executables are unsigned: launcher, CLI, and GameCompat extractor.
5. For unsigned mode the detached handoff P7S field and file must be absent, not empty/null placeholders. Signed mode keeps its existing strict certificate and timestamp checks.
6. Keep Ed25519 update-metadata signatures, checksums, BOM/SBOM, exact artifact verification, provenance, protected environment approval, and immutable publication. Unsigned executables do not mean unsigned update metadata.
7. Correct stale AdminRelease, LiveGameAcceptance, ReleaseChecklist, LaunchBlockers, and release-note claims. Do not claim the unsigned path is blocked on buying an Authenticode certificate.

The protected release environment has an update-key secret name configured. No secret value was read or copied. Credential rotation and reviewer approval still require real evidence; the presence of a secret does not prove rotation.

## Detached qualification contract

Keep release/release-readiness.json tracked at the frozen source SHA and truthful: P0-GAME-01 can remain blocked until the exact bytes have been exercised. Preserve all twelve registered gates and their identities, roles, priorities, enforcement, and accepted-risk restrictions. The five blocking gates remain P0-IP-01, P0-OSS-01, P0-PRIV-01, P0-CRED-01, and P0-GAME-01.

Add release validate-prerequisites --version --target-sha. Load contracts from that exact SHA. Require the four non-game blocking approvals; permit only P0-GAME-01 deferred for private builds. Report eligible-for-private-build, never ready. Preflight uses this named assessment, not a generic unresolved-policy bypass. Rehearsals remain permanently non-publishable.

Add bounded strict-schema release-candidate-readiness-v1.json and a redacted release-candidate-acceptance-v1.json. The final decision binds repository, version, exact source SHA, tracked readiness/schema/policy/catalog blob digests, aggregate handoff digest, and a sorted exact catalog payload inventory of name/size/SHA-256. It also binds the acceptance record digest and real reviewer evidence references. Acceptance covers the pinned build and source-SHA inventory, including the user-required gamemode matrix. Only the P0-GAME-01 row may supersede its tracked base; every other row matches exactly. Passing a test does not manufacture reviewer approval.

Add durable states preflight -> platforms-built -> built -> accepted -> staged -> dispatch-requested -> published. Built means validated candidate bytes/handoff, not release approval. release-admin qualify validates the actual local evidence and reviewed final decision, then atomically freezes their hashes in accepted state. Stage/dispatch/resume reject shortcuts from built. Any changed source, payload, handoff, receipt, evidence, or decision invalidates qualification. Never rebuild or repack after qualification; changed bytes require a new tested candidate.

Publication-grade release validate-readiness --version --target-sha --assets requires the final detached decision, with no legacy-ready or working-tree fallback. Stage the decision and bounded redacted acceptance record under the existing strict human-uploader allowlist and no-replacement rules. Verify exact bytes before protected approval, after it, and immediately before publication. Bind base/decision/evidence digests, source SHA, and effective gate summary into the BOM and verifier attestation.

Keep publication metadata downstream of the qualified payload inventory. BOM/SBOM/checksums/update metadata and the decision itself are not payload inputs; including them would create a new hash cycle. They remain independently verified and covered by final checksums/attestations.

## Regression tests before fixes

- Exactly four approved non-game blocking gates plus pending P0-GAME permits private preparation. Missing any of the four fails.
- Missing/empty/fabricated evidence, wrong SHA/version/base digest, incomplete or failed required cases, wrong game build, missing reviewer references, or stale receipts cannot qualify.
- Changed package bytes, added/removed payload, handoff changes, acceptance digest drift, and decision changes invalidate qualification.
- Other gate rows cannot change, enforcement cannot weaken, and advisory failures cannot disappear.
- Working-tree policy/schema/decision changes cannot affect exact-SHA decisions.
- Stage, dispatch, resume, direct metadata commands, and hosted finalization cannot bypass qualification.
- Rehearsal cannot be promoted. Interrupted qualification is atomic and idempotent; mismatched retries fail closed.
- Asset replacement between approval and publication fails.
- Explicit unsigned policy ignores ambient signing credentials, rejects contradictory signing flags before writes, requires all executable signatures absent and P7S absent, and retains Ed25519 verification. Signed behavior remains strict.

## Acceptance-policy reconciliation

The generic P0-GAME-01 0.x policy was narrowed to pinned-build interactive startup/clean shutdown, one first-party GameCode package loaded, and successful gamecompat verification. The builder/handoff still hard-require a full 15-case SDK acceptance suite and ten lifecycle cycles. Make policy, inventory, builder, handoff, and docs agree explicitly. Do not use generic smoke narrowing to remove the user's separately required full redesign acceptance: cold launches, generated Open Sandbox geometry/environment/spawn/kill plane, both discovery sources, authored markers, Zombies, Sandbox F5/pause, Free Play without Sandbox, restart/menu, startup/teardown failure injection, native cancellation/drain/competition. Unit tests do not prove those native or visual behaviors.

## Website dependency patches

Recheck the current dependency alerts before changing the lockfile. The 2026-09-06 triage found website/package-lock.json transitive updates available within existing ranges: fast-uri 3.1.4 -> 3.1.6; nanoid 3.3.16 -> 3.3.18; js-yaml 4.3.0 -> 4.3.1; postcss 8.5.19 -> 8.5.23. Avoid unrelated major/framework updates. Run website tests and full publication verification after the targeted lockfile repair. These are build-tool findings; website/npm is not in the Windows payload. Do not dismiss the alerts instead of fixing them.

## External prerequisites still need concrete locations

The user stated that approval/rotation records and isolated QA resources are available, but their actual paths/links and the QA host/session identity have not been provided. Obtain the non-secret records and host details; do not invent evidence IDs or approval.

- Four non-game blocking approval/rotation records with real evidence and authorized reviewer attribution.
- An isolated Windows user/session or VM with authorized installed-game access and genuine input/visual observation. An alternate launcher profile or BepInEx directory alone does not isolate Unity persistentDataPath/native saves/authentication. No supported native-save override has been established.
- The pinned Unity authoring editor 6000.0.23f1 and activated license on the release builder; the configured C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe is absent locally. Game runtime Unity 6000.0.31f1 is a separate identity, not this editor requirement.
- Pinned build toolchain including Node 24.18.0 (the currently located bundled Node is 24.19.0), Flutter 3.44.6, Dart 3.12.2, .NET SDK 10.0.301/runtime 10.0.9, MSVC and Windows SDK pins.

The acceptance runner currently writes to an ordinary install even in skip-runtime-install paths, and its Windows stop helper can kill all matching executables. Repair isolation and use retained PID/start-time/executable ownership before live work. Verify actual BepInExRoot/ManagerRoot/persistentDataPath and abort if the ordinary user data root is selected. Keep raw logs private; only bounded redacted evidence leaves the QA host.

Existing historical ZIPs and current-tree logs are not evidence for a new frozen candidate. Run scoped Dart/PowerShell regressions, format/analyze/line limits, repository audits, website publication, Windows packaging/release validation and exact-head CI. Record commands, revisions, actual evidence and pending external prerequisites in the ledger. Submit only this slice; create the slice 8 branch only after its normal green merge.
