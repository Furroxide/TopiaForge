# RC1 launch preparation

Updated 2026-09-24. **Release readiness remains blocked.** This is the maintainer entry point for `0.1.0-rc.1` preparation, the user's decisions, review requests, setup plans and retained evidence. Prepared documents are not completed reviews or executed acceptance.

The review requests pin release head `79740dc1d747e75510ca477e7be0d5c0b109a085`, tree `10b55514193727d205fe84cb62e8e03d9aa4fb90`: the release branch after the 2026-09-24 stabilization. It is not the final `main` candidate. Earlier CI applies only to its recorded source, and the earlier review baseline `4377338` is superseded.

## Current release stabilization

Robotopia's public build moved from 2409 to 2478, which failed every `--require-latest` gate. The user updated the official install, and [PR #137](https://github.com/Furroxide/TopiaForge/pull/137) retargeted RC1 to build 2478. It also repaired `topiaforge compat bump` and adapted two runtime changes found by the compatibility audit.

The following stabilization PRs were squash-merged into `release/0.1.0-rc.1` under the user's 2026-09-24 "merge when green" authorization, after every required check and CodeQL passed:

- [PR #139](https://github.com/Furroxide/TopiaForge/pull/139): launcher guidance for a game newer than a mod supports.
- [PR #131](https://github.com/Furroxide/TopiaForge/pull/131): Sandbox automation and QA provisioning tooling.
- [PR #138](https://github.com/Furroxide/TopiaForge/pull/138): two native world-loading fixes.

[Evidence](Evidence.md#build-2478-retarget-2026-09-24) records what ran and what did not. The earlier repairs, [PR #129](https://github.com/Furroxide/TopiaForge/pull/129) and [PR #130](https://github.com/Furroxide/TopiaForge/pull/130), remain integrated.

The user answered the long-open scope question: full requirements, with all game QA optional, and the sixteen Unity authoring cycles kept mandatory. The user reviewed and merged the register and tooling change personally. `P0-GAME-01` is advisory under the owner's disposition and stays blocked, UX and E2E carry accepted risk, and a candidate may record live acceptance as `not-run`. See [Decisions](Decisions.md#rc1-completion-request-2026-09-24). The final `main` merge remains the user's action in PR #119.

The 2026-09-11 provisioning retry reached Unity OnApplicationQuit but exceeded its 90-second deadline. The diagnostic launch mode is verified on a fixture player, and retry `20260911T183026Z` was staged but never dispatched. Its game copy is build 2409, so it no longer matches the pinned build. Verified 2478 copies exist, and retry `20260924T172541Z` is re-staged for build 2478 and refusal-checked, but not dispatched. See the [observer runbook](runtime-provisioning-observer.md).

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
| Separate Windows QA context and neutral build paths | [Setup plan](isolated-qa-setup-plan.md) | Optional for RC1. QA account/roots, verified build-2409 game copies (now stale against the 2478 pin), verified build-2478 copies and development inputs provisioned; privately chosen password confirmed set. Normal first sign-in and native host/device measurements confirmed; Unity persistence recorded privately during a failed shutdown attempt; clean retry and isolation admission pending. Neutral release build remains a plan |
| Actual Unity persistence before admission | [Provisioning observer](runtime-provisioning-observer.md) | Actual path recorded privately; first headless attempt **failed** on shutdown timeout and required owned-process termination. Correction reached Update and OnApplicationQuit on September 11, but shutdown still timed out. All failures and cleanup are retained. The diagnostic launch mode is verified on a fixture player. Retry `20260911T183026Z` was staged against a build-2409 copy and is now stale. Retry `20260924T172541Z` is re-staged for build 2478; one confirmed operator session, successful completion and attributable review remain |
| Complete continuation instructions | [Full completion prompt](CompletionPrompt.md) | Current scope, remaining implementation, approvals, QA state and final release sequence; reverify before acting |
| Protected governance audit token | [Secure setup instructions](governance-audit-token-setup.md) | Prepared; configuration pending |
| Update-signing seed recovery and duplicate cleanup | [Administrator checklist](update-signing-recovery-cleanup-checklist.md) | Prepared; no recovery or cleanup executed |
| All nine Sandbox workbench scenarios | [Implementation plan](sandbox-automation-implementation-plan.md), [stages 1–2](sandbox-automation-stages-1-2.md), [stages 3–6 handoff](sandbox-automation-stages-3-6.md) and [native matrix completion contract](sandbox-native-matrix-completion.md) | Integrated through PR #131. Offline and Editor evidence recorded; protocol v2 closes the nine-row gap table in source and the exact-source Editor rerun passed on 2026-09-13. Admitted native execution has not run and is optional QA |
| Native launcher and in-game UX/accessibility | [Operator handoff](native-ux-accessibility-handoff.md) | Prepared; optional (accepted-risk). User nominated as operator; admission, reviewer roles and execution pending |
| Independent player and author journeys | [Tester handoff](independent-tester-handoff.md) | Prepared; optional (accepted-risk). Testers and execution pending |

Unity `6000.0.23f1` installation and the prepared fourteen EditMode cases are complete. The sixteen candidate authoring cycles remain pending; full isolated game acceptance is optional under the owner's disposition. Credential-incident closure is deferred by the user. Neutral build setup remains a plan by explicit user choice. The native UX/accessibility operator handoff is prepared; execution remains pending. The separately authorized first two Sandbox automation stages and their applicable source tests are complete; exact coverage and residual requirements are recorded in the implementation handoff. The user subsequently authorized all remaining Sandbox stages and provisioning a standard `TopiaForgeQA` account on the drive with most free space (`D:\TopiaForgeQA`). The user confirmed the existing game binary source, themselves as operator, main display and default output audio; microphone recording remains off. The standard account, two verified game copies and independently verified development inputs are provisioned. The user has completed normal first sign-in and the initialized Windows profile was observed. The native host probe measured the QA identity, known folders, main display, input-device identifiers and default render endpoint. A verified outbound firewall rule applies only to the QA game executable. The separately authorized headless [provisioning observer](runtime-provisioning-observer.md) recorded the actual Unity persistence path privately with the manager inactive. The attempt **failed** because the game did not exit before its deadline; the original game process was force-terminated and exit was then confirmed. The correction passed source validation, but its retry timed out waiting for the active QA desktop before broker startup; the one-time task was removed. The September 11 retry then reached Unity OnApplicationQuit but still required forced termination after the deadline; its task was removed. A successful native retry remains required; no acceptance acknowledgement or successful provisioning result was produced. Native Sandbox execution and a performed (`run`) candidate acceptance still require actual isolation/device admission and the private reviewed records. A `not-run` candidate has neither; its acceptance record states that no live acceptance ran and cites the owner's disposition.

## Authoritative contracts and execution order

The [machine readiness register](../../../release/release-readiness.json), [release policy](../../../release/release-policy.json) and [catalog](../../../release/catalog.json) govern validation; this hub changes none of them. [LaunchBlockers](../../LaunchBlockers.md) contains detailed and historical dispositions. [RepositoryGovernance](../../RepositoryGovernance.md) defines protected integration and publication. [LiveGameAcceptance](../../LiveGameAcceptance.md) defines current native evidence, and the [redesign ledger](../gamemode-contract/Status.md) retains revision-specific history.

Obtain the four non-game approvals and integrate reviewed source/gate changes normally. Freeze the actual final two-parent `main` merge, run prerequisite admission from a clean checkout equal to `origin/main`, then build and exercise exact candidate bytes. Review detached records and qualify before staging a signed annotated tag and matching draft. Publication requires its separate protected approval. Rehearsal is nonpublishing and cannot qualify, tag, stage or dispatch. RC1 is Windows x64 only; adding a platform to policy does not restore the retired Proton runner.

## Repository and private evidence boundary

These maintainer documents live under `docs/internal`, outside the public guide catalog and release payloads. They contain safe status, required role coverage and proposed procedures. Actual approvals, account identities, isolation records, raw game/Editor logs, incident details and credentials belong in restricted private storage, not Git.

For this local preparation session, the original private review packet and Unity evidence have been copied and hash-verified into the ignored `.dart_tool/rc1-review/` directory inside the repository. The original sibling-worktree packet remains intact. The private import receipt records the copied inventory. A clean clone intentionally lacks private evidence; obtain it from its authorized custodian. Tracked documents do not depend on private-file links.
