# RC1 completion continuation prompt

Prepared 2026-09-11 from the current launch documents, local QA receipts and a live read of PR #119. This is a continuation prompt, not an approval, completed review or release decision. Reverify observations before acting. Copy the text below into the continuing task.

---

Continue TopiaForge RC1 preparation and finish all remaining authorized Sandbox automation and launch-checklist work. Work from `C:\Users\vanst\Code\TopiaForge`, including the original referenced review packet at `C:\Users\vanst\Code\TopiaForge-gm\.dart_tool\rc1-review\NextActions.md`. Carry the work through implementation, meaningful verification, integration preparation and the release prerequisites that can actually be completed. Do not declare the launch complete while required execution or owner decisions remain outstanding.

1. Establish the current state and preserve existing work.

Read `AGENTS.md` and start at `docs/internal/launch/README.md`. Read `NextActions.md`, `Decisions.md`, `Evidence.md`, `ReviewGates.md`, `docs/internal/RC1PrereleaseHandoff.md`, `docs/LaunchBlockers.md`, `docs/ReleaseChecklist.md`, `docs/AdminRelease.md`, `docs/RepositoryGovernance.md`, `docs/LiveGameAcceptance.md`, and the machine readiness, policy and catalog files under `release/`. Follow all referenced nested reviewer requests, setup plans, Sandbox implementation/handoff documents, UX and independent-tester handoffs, signing recovery checklist and governance-token instructions.

Reverify Git state, worktrees, current remote refs, PRs and source-specific CI. At the latest inspection, the primary checkout was a dirty detached development tree at `437733854795c11e684eac9d59d6bc52ada9516e`. PR #119 was open and draft from `release/0.1.0-rc.1` to `main`, at `0ea59e7941b1d56f515ac34f89819f31f16c7137`, with all 62 reported checks successful. PRs #129 and #130 had integrated release stabilization; the documented full CI, CodeQL and packaging dry runs passed for that release head. The unfinished local automation/provisioning changes are separate and are not covered automatically by those results.

Preserve unrelated local work, including `assets/launcher/`, other worktrees and branding changes. Reconcile overlapping production fixes before integrating local automation through ordinary reviewed changes. Use an isolated checkout where appropriate; do not reset or blindly switch the dirty checkout, overwrite snapshots, duplicate already integrated fixes or claim old test evidence covers new source.

2. Preserve my decisions and minimize interruptions.

The remaining Sandbox stages, applicable tests and isolated QA provisioning are already authorized. Unity 6000.0.23f1 is installed and the original fourteen EditMode cases passed; do not reinstall it or repeat password/account setup unnecessarily. The standard account is `TopiaForgeQA`, with heavy QA storage on `D:\TopiaForgeQA`. The verified existing Robotopia build-2409 binary source, me as operator, main display and default output audio were approved. Microphone recording remains off. Do not import personal saves, tokens, configuration, caches or licence files.

Credential-incident closure remains explicitly deferred. Neutral release-build roots, fresh SDK and dedicated cache provisioning remain plan-only. Signing recovery and plaintext cleanup were authorized only as a prepared checklist. Do not treat this continuation request as reversal of those decisions. Identify how they block completion, keep them open and obtain an explicit scope change before dependent execution. Never read the QA bootstrap credential, signing seed, secret values or old incident logs into chat.

The current decision ledger also records an unanswered full-versus-reduced experimental-release question and a merge authorization limited to PR #130. Verify the originating decision context if needed. Do not select an unanswered option, downgrade gates because this is a prerelease, or infer authority for the final main merge. Complete independent authorized work while required decisions wait.

Wait indefinitely for my explicit reply to any question. Never choose a default or treat elapsed time as consent. Prepare concrete reviewable work before requesting a decision. Do not contact reviewers or send messages to others without explicit authorization. Keep final main merge for me; use actual authorization and protected approval for subsequent external release actions.

Avoid repeated five-minute account-switch rounds. Prepare and verify scripts, source, inputs, permissions, dependencies and diagnostics before involving me. Coordinate one bounded operator session for all ready checks where possible, with clear progress and completion signals. Confirm my availability before starting a short desktop-wait window. If another native attempt fails, preserve its diagnostics and investigate before requesting another switch. Execution deadlines remain bounded; those deadlines are not deadlines for my answers.

3. Finish QA provisioning and prove the shutdown correction natively.

Read `docs/internal/launch/runtime-provisioning-observer.md` and the actual private receipts. Account initialization and host identity/known-folder/device metadata succeeded. The first runtime attempt wrote a correlated actual Unity persistence-path observation with the manager inactive, but the game exceeded its 90-second deadline. Its original process was force-terminated and exit was confirmed. That attempt remains failed.

The deferred-first-Update quit correction passed 53 focused checks and all six required C# build/regression commands. Its v3 receipt is `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-observer-quit-validation-v3/final-source-receipt-v3.json`. Source snapshots, exact loader hashes and the unchanged complete broker bundle are retained privately.

The corrected retry was staged in `D:\TopiaForgeQA\tools\runtime-probe-20260909T215900Z` for the separate game at `D:\TopiaForgeQA\game-provisioning-20260909T215734Z`. It timed out at 22:13:23Z on September 9 waiting for an active, unlocked QA desktop. `brokerStarted` was false: neither broker nor game ran, so this attempt did not test the shutdown correction. The temporary task was removed. Preserve both failure records and the original failed game at `D:\TopiaForgeQA\game`.

The September 11 retry completed with a failed result at 17:17:04Z. Fresh identity measurement passed; the game recorded the correlated observation, reached the first Update quit request and OnApplicationQuit, but still exceeded 90 seconds and required original-process termination. Exit was confirmed, the task was removed, and nineteen private files were retained under `runtime-retry-failed-20260911T171020Z`. No recovery marker or remaining game/broker was found. The broker console was empty and no current Player.log existed; the runbook records the -nographics diagnostic limitation. Diagnose and verify a logging-capable bounded approach before asking for another operator session; no further attempt is queued.

Revalidate current identity, logon/session, physical roots, ACLs, source/runtime/package/broker hashes, network isolation and cleanup state. Old session identifiers and immutable request/output files must not be blindly reused. The game copy unused by the September 9 desktop-wait attempt was subsequently consumed by the failed September 11 runtime attempt. Preserve it as failed evidence; it now contains manager staging and must not be treated as an unused copy or wiped for a retry. Keep the exact-game outbound blocks effective, and preserve read leases, original-process ownership, bounded cleanup and failure receipts. Never substitute an expected path, forged acknowledgement, success flag or forced termination for successful unforced completion.

After actual successful measurement, obtain an attributable private provisioning review and device admission/reservation using the exact repository contracts. A valid JSON shape, operator nomination or raw path observation is not reviewer approval. Keep sensitive identity/device details and raw records in private storage, with only safe references in tracked documents.

4. Complete the remaining Sandbox automation implementation and execution.

Read the full nine-row gap table in `docs/internal/launch/sandbox-automation-stages-3-6.md` and the implementation plan. Existing scenario names and repeated partial recipes do not constitute complete coverage. Finish the required native actions, independent observations and failure detection for:

- Routing across menu, loading/preparing/stopping, other modes, wrong/competing owners and duplicate toggles.
- Catalog/editing, including expected inventories, categories, empty searches, offscreen scrolling, rendered selection, precise transforms, duplicate/remove and Undo.
- Borrowed-robot restoration, external-writer conflicts, disappearing targets, partial application and temporary-asset destruction.
- Source teardown, pending callbacks, borrowed/edited leases and unrelated owners that must survive.
- Hide/F5/close/reopen behavior, retained edits and graphs, real movement/camera restoration and text-focus isolation.
- Persistence refusal, rendered reasons, in-operation checkpoints and applicable native denial/revocation cases.
- Graph branching, real interaction, borrowed-target rollback, cancellation/fault/reentrancy/late completion, conversation behavior and independent audio onset/cessation/control-cue checks.
- All supported lifecycle routes, scene/target changes, retired controllers and callback identities, ownership and input cleanup.
- Ten complete lifecycle cycles containing the required branches, with independently measured identities and destruction/restoration barriers rather than counts alone.

Implement reviewed visual baselines and tolerances, accessibility profiles, contrast/scale/reduced-motion/focus assertions, robust native control access and meaningful fault injection that proves the oracles detect failures. Keep genuine product limitations explicit: unsupported vehicles, global mutation, real remote sessions or unavailable backend behavior must not be turned into passing fake/loopback branches. Surface product-scope decisions where a required capability has no shipping implementation.

Rerun the pinned Editor against the exact final fixture source, then run the complete admitted native matrix. Finish source tests, independent verifiers and CI integration. Preserve the distinction between offline regressions, Editor results, native development evidence and final-candidate acceptance. Development annexes remain `qualifiesRelease: false`.

5. Close the engineering validation and review prerequisites.

Run the applicable `AGENTS.md` checks on the final integrated source, including the relevant C# harnesses, Dart/Flutter tests and analysis, Windows build and file-size conventions. Diagnose the dirty development tree's outstanding launcher-data SDK restore timeout and lack of a complete passing CLI suite without increasing limits, suppressing failures or erasing evidence merely to obtain green results. Explain any platform-only skips under the actual policy.

Reconcile the old Windows Dartdoc/partial-documentation failures with later passing full Linux documentation/reference/search CI at the separate release head. Update stale action wording by exact source coverage. New integrated automation still needs its own applicable fresh checks; already verified unchanged work need not be repeatedly rerun without cause. Verify payload exclusions, dependency provenance, notices and all links/examples affected by the final changes.

Obtain real role-complete approval records for `P0-IP-01`, `P0-OSS-01` and `P0-PRIV-01`. `P0-CRED-01` also blocks private candidate preparation, but remains deferred until I explicitly resume it and the credential/security owners supply actual closure evidence. Follow `ReviewGates.md`; prepared requests, ownership assumptions and disclaimers do not clear gates. Integrate only valid safe references through normal review before the final freeze. Recheck actual candidate redistribution inventories and notices when those bytes exist.

Complete or obtain policy-permitted dated owner dispositions for `P0-WIN-01`, `P0-HOST-01`, `P0-CAND-01`, `P1-UX-01` and `P1-E2E-01`. Preserve existing approved TRUST and SUPPORT dispositions unless their scope changes. Native UX/accessibility and independent player/author journeys need actual operators/reviewers, execution and attributable results; do not contact people or invent attestations.

Have the administrator securely configure `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` if still absent: a dedicated fine-grained PAT restricted to Furroxide/TopiaForge, Administration read and Actions read, with implicit Metadata read and no write permissions. Verify only safe configuration metadata. Complete protected signing-seed recovery and verified exact plaintext cleanup only under their required explicit authorizations. Never print secrets or silently rotate the verified GitHub-held seed. Obtain explicit authorization before provisioning the plan-only neutral release-build resources.

6. Complete the actual release sequence when prerequisites permit.

Keep the current RC1 scope: Windows x64, Robotopia build 2409, experimental prerelease, thirteen first-party mod packages and two embedded VPM packages. Unsigned Windows executables do not waive Ed25519 update signatures, checksums, signed annotated tags or exact-byte verification. Reverify these counts against the final catalog and policy.

Integrate reviewed source and genuine gate changes normally. Refresh PR #119 with the final source, validation and remaining decisions; leave auto-merge off and prepare the final main merge for me. After I merge, verify the required two-parent topology, exact checked release parent and matching tree. Follow the required normal main-to-dev synchronization PR process in `RepositoryGovernance.md`; do not push directly to protected branches.

Freeze the actual final main SHA in a clean checkout equal to `origin/main`. All four non-game blocking approvals must be present before private candidate construction; only GAME may await the candidate. Use the reviewed isolated QA record and explicit neutral source/state/cache paths consistently. Run prerequisite validation and administrator preflight, require `eligible-for-private-build`, then build exact candidate bytes.

Complete the required fifteen SDK cases, ten game lifecycle cycles, thirty-six gamemode cases, sixteen pinned-Unity candidate authoring cycles and all mandatory native acceptance. Obtain the actual detached readiness/acceptance records and complete `P0-GAME-01` for that candidate. Run qualification and require `accepted`; any payload/source change requires the appropriate new build and acceptance.

Complete the required nonpublishing rehearsal and governance audit. Do not delete obsolete environments before the rehearsal and separate applicable authorization. Require every blocking gate closed, advisory evidence/dispositions complete, no critical/high defects or unexplained failures/warnings/flakes, zero skips in required final acceptance, policy-compliant final test results, and explicit project-owner/release-manager SHIP approval.

Only with actual authorization and all prerequisites satisfied, use the administrator stage/dispatch/resume flow. Stage the signed annotated tag and matching draft after qualification; use the protected release approval and exact run binding for publication. Verify the policy's eighteen human-owned assets plus five generated metadata assets, immutable published bytes, checksums, Ed25519 metadata, verifier attestation and prerelease exclusion from stable feeds. Revalidate readiness at every prescribed boundary. Never bypass protection, manually redispatch an uncertain request or present a dry run as publication.

7. Keep the repo and handoff accurate throughout.

Keep the launch hub, actions, decisions, evidence, nested runbooks, prerelease handoff and original sibling review view consistent. Retain raw logs, actual review/provisioning records and credentials outside Git/public assets. Preserve failed attempts and source-specific receipts. Do not change readiness merely to make progress appear complete.

Use bounded independent subagents where useful. Give concise progress updates and continue all independent authorized work while waiting for explicit user input. At each real handoff report completed work, exact source/artifact identity, tests and actual outcomes, remaining implementation, external decisions and the next concrete action. Full completion requires the actual required implementation, execution, approvals and final release verification; otherwise state the remaining blockers precisely and leave them open.
