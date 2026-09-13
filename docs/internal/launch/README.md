# RC1 launch preparation

Updated 2026-09-11. **Release readiness remains blocked.** This is the maintainer entry point for `0.1.0-rc.1` preparation, the user's decisions, review requests, setup plans and retained evidence. Prepared documents are not completed reviews or executed acceptance.

The source/evidence baseline is release commit `437733854795c11e684eac9d59d6bc52ada9516e`, tree `976326d02fd68b19a2e10bde923e15510df441a6`. It is not the final `main` candidate. The local documentation refresh and Sandbox source changes need normal review and integration; earlier CI applies to its recorded source only.

## Current release stabilization

The website-security and prerelease-documentation repair in
[PR #129](https://github.com/Furroxide/TopiaForge/pull/129) and the production
Sandbox cleanup/error repair in [PR #130](https://github.com/Furroxide/TopiaForge/pull/130)
are integrated at release head `0ea59e7941b1d56f515ac34f89819f31f16c7137`.
Both stabilization PRs passed their required hosted checks and full 16-job CI.
PR #130's final runtime helper also passed bounded timeout/held-pipe probes.
Both final release-head CI runs passed all 16 jobs, both CodeQL runs passed,
and the packaging dry run passed all 11 jobs. PR #119 records the exact results. Existing local automation/provisioning work is
preserved separately. See [Decisions](Decisions.md) for the pending scope
clarification and limited PR #130 merge authorization, and [Evidence](Evidence.md)
for source-specific results, the subsequent native shutdown failure and remaining
requirements. The final `main` merge remains the user's action in PR #119.

The 2026-09-11 retry ran the game and reached Unity OnApplicationQuit, but again exceeded the 90-second deadline. Original-game exit was confirmed after forced cleanup; the task was removed. Successful provisioning and admission remain blocked; investigate the shutdown diagnostics before another operator session. See the [observer runbook](runtime-provisioning-observer.md).

## Start here

1. Read [NextActions](NextActions.md) for every open item, owner role and dependency.
2. Read [Decisions](Decisions.md) before asking for direction. Unanswered questions wait indefinitely for an explicit reply; time passing never selects a default. Independent authorized work may continue.
3. Use [ReviewGates](ReviewGates.md) to obtain real attributable IP, OSS, privacy and credential-closure records.
4. Use the [current RC1 handoff](../RC1PrereleaseHandoff.md), [administrator runbook](../../AdminRelease.md) and [release checklist](../../ReleaseChecklist.md) when prerequisites permit execution.
5. Record observations under their exact source/candidate identity; [Evidence](Evidence.md) distinguishes source tests, local setup, private records and candidate acceptance.

## Prepared material

| Purpose | Document | Current outcome |
| --- | --- | --- |
| IP, names, integration and retained assets | [Reviewer request](ip-naming-review-request.md) | Prepared; decisions pending |
| Third-party redistribution | [Reviewer request](oss-redistribution-review-request.md) | Prepared; decisions pending |
| Remote AI/token/audio privacy and backend use | [Reviewer request](privacy-backend-review-request.md) | Prepared; decisions pending |
| Separate Windows QA context and neutral build paths | [Setup plan](isolated-qa-setup-plan.md) | QA account/roots, verified game copies and development inputs provisioned; privately chosen password confirmed set. Normal first sign-in and native host/device measurements confirmed; Unity persistence recorded privately during a failed shutdown attempt; clean retry and isolation admission pending. Neutral release build remains a plan |
| Actual Unity persistence before admission | [Provisioning observer](runtime-provisioning-observer.md) | Actual path recorded privately; first headless attempt **failed** on shutdown timeout and required owned-process termination. Correction reached Update and OnApplicationQuit on September 11, but shutdown still timed out. All failures and cleanup are retained; diagnose before a new run, then obtain successful completion and attributable review |
| Complete continuation instructions | [Full completion prompt](CompletionPrompt.md) | Current scope, remaining implementation, approvals, QA state and final release sequence; reverify before acting |
| Protected governance audit token | [Secure setup instructions](governance-audit-token-setup.md) | Prepared; configuration pending |
| Update-signing seed recovery and duplicate cleanup | [Administrator checklist](update-signing-recovery-cleanup-checklist.md) | Prepared; no recovery or cleanup executed |
| All nine Sandbox workbench scenarios | [Implementation plan](sandbox-automation-implementation-plan.md), [stages 1–2](sandbox-automation-stages-1-2.md) and [stages 3–6 handoff](sandbox-automation-stages-3-6.md) | Offline and Editor evidence recorded; native runner/broker/CI tooling exists. Full native matrix implementation/execution and exact-source Editor rerun remain incomplete |
| Native launcher and in-game UX/accessibility | [Operator handoff](native-ux-accessibility-handoff.md) | Prepared; user nominated as operator, QA account/roots provisioned; admission, reviewer roles and execution pending |
| Independent player and author journeys | [Tester handoff](independent-tester-handoff.md) | Prepared; testers and execution pending |

Unity `6000.0.23f1` installation and the prepared fourteen EditMode cases are complete. The sixteen candidate authoring cycles and full isolated game acceptance remain pending. Credential-incident closure is deferred by the user. Neutral build setup remains a plan by explicit user choice. The native UX/accessibility operator handoff is prepared; execution remains pending. The separately authorized first two Sandbox automation stages and their applicable source tests are complete; exact coverage and residual requirements are recorded in the implementation handoff. The user subsequently authorized all remaining Sandbox stages and provisioning a standard `TopiaForgeQA` account on the drive with most free space (`D:\TopiaForgeQA`). The user confirmed the existing game binary source, themselves as operator, main display and default output audio; microphone recording remains off. The standard account, two verified game copies and independently verified development inputs are provisioned. The user has completed normal first sign-in and the initialized Windows profile was observed. The native host probe measured the QA identity, known folders, main display, input-device identifiers and default render endpoint. A verified outbound firewall rule applies only to the QA game executable. The separately authorized headless [provisioning observer](runtime-provisioning-observer.md) recorded the actual Unity persistence path privately with the manager inactive. The attempt **failed** because the game did not exit before its deadline; the original game process was force-terminated and exit was then confirmed. The correction passed source validation, but its retry timed out waiting for the active QA desktop before broker startup; the one-time task was removed. The September 11 retry then reached Unity OnApplicationQuit but still required forced termination after the deadline; its task was removed. A successful native retry remains required; no acceptance acknowledgement or successful provisioning result was produced. Sandbox and candidate acceptance still require actual isolation/device admission and the private reviewed records.

## Authoritative contracts and execution order

The [machine readiness register](../../../release/release-readiness.json), [release policy](../../../release/release-policy.json) and [catalog](../../../release/catalog.json) govern validation; this hub changes none of them. [LaunchBlockers](../../LaunchBlockers.md) contains detailed and historical dispositions. [RepositoryGovernance](../../RepositoryGovernance.md) defines protected integration and publication. [LiveGameAcceptance](../../LiveGameAcceptance.md) defines current native evidence, and the [redesign ledger](../gamemode-contract/Status.md) retains revision-specific history.

Obtain the four non-game approvals and integrate reviewed source/gate changes normally. Freeze the actual final two-parent `main` merge, run prerequisite admission from a clean checkout equal to `origin/main`, then build and exercise exact candidate bytes. Review detached records and qualify before staging a signed annotated tag and matching draft. Publication requires its separate protected approval. Rehearsal is nonpublishing and cannot qualify, tag, stage or dispatch. RC1 is Windows x64 only; adding a platform to policy does not restore the retired Proton runner.

## Repository and private evidence boundary

These maintainer documents live under `docs/internal`, outside the public guide catalog and release payloads. They contain safe status, required role coverage and proposed procedures. Actual approvals, account identities, isolation records, raw game/Editor logs, incident details and credentials belong in restricted private storage, not Git.

For this local preparation session, the original private review packet and Unity evidence have been copied and hash-verified into the ignored `.dart_tool/rc1-review/` directory inside the repository. The original sibling-worktree packet remains intact. The private import receipt records the copied inventory. A clean clone intentionally lacks private evidence; obtain it from its authorized custodian. Tracked documents do not depend on private-file links.
