# Launch preparation decisions

Recorded from explicit user replies on 2026-09-09. These are task-scope decisions, not reviewer attestations or release-gate approvals. No exact reply times are inferred.

| Topic | Explicit direction | Effect |
| --- | --- | --- |
| Working method | Complete feasible NextActions items and referenced nested documents, asking how to proceed for each | Prepare concrete choices and retain each answer |
| Pinned Unity | Authorize installation, licence acceptance, association change and fourteen tests using existing licence | Completed; exact test evidence recorded |
| IP/naming | Prepare a review request for the user to give reviewers | Request prepared; approval pending |
| OSS redistribution | Prepare the redistribution review request | Request prepared; approval pending |
| Privacy/backend | Yes, prepare the privacy/backend review request | Request prepared; approval pending |
| Credential-incident closure | Defer credential-incident closure | Blocking gate unchanged; do not reopen secret-bearing logs |
| Isolated QA | Prepare a setup plan for a new isolated QA environment | Initial plan prepared; later QA provisioning authorization below supersedes this initial limit |
| Governance audit token | Prepare secure setup instructions | Guide prepared; no token created or entered |
| Signing-key recovery and duplicates | Prepare the recovery and cleanup checklist | Checklist prepared; no key access, recovery test or deletion |
| Neutral build roots/SDK | Leave neutral build setup as a prepared plan | No provisioning or fresh SDK download |
| Sandbox additional QA | Asked whether automated testing is possible; then requested a plan to fully implement automation at the expected standard | Full implementation plan prepared; no implementation or test execution authorized by that reply |
| Native UX/accessibility | Replied “Yes, continue. Don't hesitate to ask questions.” to the offered operator handoff | Initial handoff prepared; later QA/operator/source/device replies below supersede that preparation boundary |
| First two Sandbox implementation stages | **“Implement the first two stages.”** Authorize the nine-scenario specification/verifier and offline lifecycle/rollback regressions, including applicable tests | Implemented and verified locally; see the [implementation handoff](sandbox-automation-stages-1-2.md) and [Evidence](Evidence.md#sandbox-automation-stages-1-2-verification). No new Editor/native execution or provisioning authorized |
| Remaining Sandbox stages | **“Implement all remaining stages.”** | Stages 3–6 implementation and applicable execution are authorized, including the installed pinned Editor; native acceptance still requires measured QA admission and device/source details. Existing gate approvals and publication remain separate |
| QA provisioning | **“Provision it (on the drive with the most empty space) with an appropriate name.”** | Created the standard `TopiaForgeQA` account and restricted QA roots under `D:\TopiaForgeQA`, selected from measured fixed NTFS free space. Normal profile initialization is confirmed; native host/device metadata and the QA-only outbound block are verified. Actual Unity persistence was recorded privately during the first headless attempt, which failed on shutdown timeout. A validated clean retry and provisioning review remain required |
| QA binary source, operator and devices | **“Use that source, operator and devices.”** | Copy only verified game binaries from the existing build-2409 Robotopia installation; user is human operator, main display and default render audio endpoint are authorized. Both separate copies are verified; saves, tokens, personal configuration and microphone recording are excluded |
| QA password initialization | **“Please trigger it for me”**, then **“It is set”** | Reopened the local secure prompt; completion receipt confirms the privately chosen password was set at 20:02:57Z. No password value read. Windows profile was still absent at that checkpoint; the later first-sign-in confirmation below supersedes it |
| QA first sign-in | **“I've logged in”** | Normal first sign-in confirmed; Windows profile metadata shows an initialized, loaded QA profile. The initialized QA identity, native known folders and device metadata were subsequently measured during an active session. The later headless runtime attempt recorded a private persistence path but failed on shutdown; successful unforced completion and reviewed admission remain required |
| QA host-probe return | **“I switched back now to the current user”** | Successful host-probe and task-completion receipts confirm measurement in the standard QA session and owned-process exit. No input, capture, microphone or game execution occurred. This is preparation evidence, not admission |
| Independent player/author QA | Prepare an independent-tester handoff | Handoff prepared; no tester contacted |
| Repository documentation | Ensure the repo contains all blocker/checklist information needed for launch and related information is up to date; continue the task | Consolidate durable maintainer documents, retain private evidence locally, correct stale related documentation |
| Waiting for answers | Wait indefinitely for explicit replies; never select a default or proceed because time elapsed; ask in plain chat and end the turn if necessary | Stop work dependent on an unanswered question; independent previously authorized work may continue |

## RC1 release preparation request — 2026-09-10

The user requested completing preparation for `release/0.1.0-rc.1`, presenting
the final merge for them to perform, and subsequent GitHub publication as an
early prerelease of `0.1.0`. Ordinary source fixes, validation, stabilization
PRs and honest prerelease documentation are within this work. The final
`main` merge remains the user's action; publication still follows its actual
qualification and protected approval requirements.

A clarification asking whether to retain the full launch requirements or
prepare a reduced experimental-release scope is **awaiting an explicit reply**.
Neither option has been selected. No gate or previous deferred item was changed
by asking this question, and no reviewer approval was inferred from release intent.

The user subsequently explicitly replied **“Authorize PR #130 merge only”**
to the stabilization merge request. PR #130 was normally squash-merged into
`release/0.1.0-rc.1`; this answer does not authorize the final `main` merge,
select the experimental scope, clear a release gate or change a deferred item.

For subsequent sessions, preserve these choices until the user changes them. A general request to update documentation is not approval to clear a gate, install a QA environment, access signing keys, send review requests to others, merge, tag or publish. Do not ask again for scope already authorized; do ask for genuinely missing inputs when the concrete next action needs them.
