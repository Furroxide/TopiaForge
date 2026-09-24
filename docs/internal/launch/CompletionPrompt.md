# RC1 completion continuation prompt

Prepared 2026-09-24 from the current launch documents, local QA receipts and live reads of the RC1 pull requests. This is a continuation prompt, not an approval, completed review or release decision. Reverify observations before acting. Copy the text below into the continuing task.

---

Continue TopiaForge RC1 preparation for `0.1.0-rc.1` and finish the remaining launch-checklist work that is authorized. Work from `C:\Users\vanst\Code\TopiaForge`; the original referenced review packet is at `C:\Users\vanst\Code\TopiaForge-gm\.dart_tool\rc1-review\NextActions.md`. Carry the work through implementation, verification, integration preparation and whatever release prerequisites can actually be completed. Do not declare the launch complete while required execution or owner decisions remain outstanding.

1. Establish the current state and preserve existing work.

Read `AGENTS.md` and start at `docs/internal/launch/README.md`. Then read:

- `NextActions.md`, `Decisions.md`, `Evidence.md` and `ReviewGates.md`.
- `docs/internal/RC1PrereleaseHandoff.md`.
- `docs/LaunchBlockers.md`, `docs/ReleaseChecklist.md`, `docs/AdminRelease.md`, `docs/RepositoryGovernance.md` and `docs/LiveGameAcceptance.md`.
- The machine readiness, policy and catalog files under `release/`.

Follow the nested reviewer requests, setup plans and handoffs they reference.

Reverify Git state, worktrees, remote refs, PRs and source-specific CI; the observations below may be stale. At the 2026-09-24 handoff:

- The primary checkout was clean at `437733854795c11e684eac9d59d6bc52ada9516e`. The uncommitted FlyBrain Robot Laboratory work that had filled it was snapshotted to the local, unpushed branch `feat/flybrain-robot-laboratory` (worktree `C:\Users\vanst\Code\TopiaForge-flybrain`). It is not part of RC1.
- `release/0.1.0-rc.1` had integrated the build-2478 retarget (#137), the newer-build launcher guidance (#139), the Sandbox automation and QA provisioning tooling (#131) and two native world-loading fixes (#138).
- PR #119 (`release/0.1.0-rc.1` to `main`) remained a draft with auto-merge off.
- The game-QA policy PR was open for the user's review.

Preserve unrelated local work and other worktrees. Do not reset or blindly switch a checkout another session may be using. Do not claim old test evidence covers new source.

2. Preserve my decisions and minimize interruptions.

The 2026-09-24 replies are recorded in `Decisions.md`:

- **Scope:** full requirements, with all game QA optional. IP, OSS, PRIV and CRED stay blocking. `P0-GAME-01` becomes advisory with an owner disposition; `P1-UX-01`, `P1-E2E-01` and the Sandbox native matrix get owner dispositions. That change exists only as the policy PR, and it takes effect only when I merge it; until then the register is unchanged.
- **Unity cycles:** the sixteen pinned-Unity authoring cycles stay mandatory.
- **Merge authorization:** "merge when green" covered only the named stabilization PRs, the QA copy-script PR and the launch-documentation PR. It never covers the policy PR or PR #119.
- **QA copies:** fresh 2478 copies are authorized. The provisioning retry may be re-staged but not dispatched.

Earlier decisions stand:

- Credential-incident closure is deferred.
- Neutral release-build roots, a fresh SDK and a dedicated cache stay plan-only.
- Signing recovery and plaintext cleanup were authorized only as a prepared checklist.

Never read the QA bootstrap credential, signing seed, secret values or old incident logs into chat.

Wait indefinitely for my explicit reply to any question. Never choose a default or treat elapsed time as consent. Prepare concrete reviewable work before requesting a decision. Do not contact reviewers or send messages to others without explicit authorization. Keep the policy PR and the final `main` merge for me.

3. Keep the game build current.

`gameBuild.requireLatestAtRelease` stays on, and the finalizer re-probes `https://builds.tomatocake.dev/latest-build.json`. Robotopia has published several builds a month. Before every push and every release phase, compare the public latest id with `.github/robotopia-game-build.json`. If it moves past the pin, stop and ask me to update the official install. Then:

1. Measure the install read-only.
2. Run `topiaforge compat bump` with its five inputs and review its unlisted-mention report.
3. Repeat the P2-COMPAT-01 audit: `gamecompat verify`, `audit --strict`, a full surface diff and a reviewed baseline refresh.
4. Adapt any binding or runtime change the diff shows.

Name-only bindings can hide signature changes, as the 2478 Health change showed. Constrain the signatures of anything the loader or mods call.

4. Optional QA, only if I choose to run it.

Under my decision, live game acceptance, native UX/accessibility, independent player/author journeys and the Sandbox native matrix are optional once the policy PR merges. The provisioning history is retained as evidence: three failed attempts, including forced termination after OnApplicationQuit on 2026-09-11, the verified diagnostic launch mode, and the staged but undispatched retry `20260911T183026Z`. Both existing QA game copies are build 2409 and no longer match the pin.

Optional execution needs:

- The verified 2478 copies `source-game-2478` and `game-2478`, which already exist. Saves, tokens, personal configuration and microphone recording stay excluded.
- The re-staged, refusal-checked retry `20260924T172541Z`. Its v6 source checkpoint is at `37f9088`, so any later loader or broker change needs a new checkpoint (see the observer runbook).
- One operator session that I confirm.
- A successful unforced exit and an attributable provisioning review.

Never substitute an expected path, forged acknowledgement, success flag or forced termination for successful completion. Development annexes stay `qualifiesRelease: false`.

5. Close the review prerequisites.

`P0-IP-01`, `P0-OSS-01` and `P0-PRIV-01` need real role-complete approval records. The three prepared requests are pinned to the stabilized release head, and I send them. Follow `ReviewGates.md`: prepared requests, ownership assumptions and disclaimers do not clear gates. Integrate only valid safe references through normal review before the final freeze. `P0-CRED-01` remains deferred until I resume it and the credential and security owners supply actual closure evidence.

Several items need the administrator:

- Configure `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` if it is still absent.
- Complete the protected signing-seed recovery and the verified plaintext cleanup, each under its own explicit authorization.
- Authorize the plan-only neutral build resources before any `release-admin.ps1` phase.

`P0-WIN-01`, `P0-HOST-01` and `P0-CAND-01` need completion or policy-permitted dated owner dispositions at SHIP time; do not invent them. Preserve the approved TRUST and SUPPORT dispositions.

6. Complete the actual release sequence when prerequisites permit.

RC1 scope is Windows x64, Robotopia build 2478, experimental prerelease, thirteen first-party mod packages and two embedded VPM packages. Unsigned Windows executables do not waive Ed25519 update signatures, checksums, signed annotated tags or exact-byte verification. Reverify these counts against the final catalog and policy.

Refresh PR #119 with the final source, validation and remaining decisions; leave auto-merge off. After I merge:

1. Verify the two-parent topology, the exact checked release parent and the matching tree.
2. Follow the normal `main`-to-`dev` synchronization in `RepositoryGovernance.md`.
3. Freeze the actual final `main` SHA in a clean checkout equal to `origin/main`.

All four non-game blocking approvals must be present before private candidate construction. Then run prerequisite validation and administrator preflight, which must report `eligible-for-private-build`, and build the exact candidate bytes.

The sixteen Unity authoring cycles always run. The live game acceptance (fifteen SDK cases, ten game cycles, thirty-six gamemode cases) is required unless the policy PR has merged. After that merge it may run, or the build records `-LiveGameAcceptance not-run` truthfully with the owner disposition. Qualification must record `accepted`, and any payload change needs a new build.

Complete the nonpublishing rehearsal and governance audit. SHIP requires all of:

- Every blocking gate closed.
- Advisory evidence or dispositions complete.
- No critical or high defects, and no unexplained failures, warnings, flakes or skips.
- An explicit project-owner and release-manager decision.

Only with actual authorization, use the administrator stage, dispatch and resume flow. Verify the eighteen human-owned assets plus five generated metadata assets, immutable bytes, checksums, Ed25519 metadata, the verifier attestation and exclusion from stable feeds. The window from freeze to publication must close before Robotopia's next update, or step 3 repeats.

7. Keep the repo and handoff accurate throughout.

Keep the launch hub, actions, decisions, evidence, nested runbooks and prerelease handoff consistent. Retain raw logs, actual review and provisioning records and credentials outside Git and public assets. Preserve failed attempts and source-specific receipts. Do not change readiness merely to make progress appear complete. At each real handoff, report:

- Completed work and the exact source and artifact identities.
- Tests and their actual outcomes.
- Remaining implementation and external decisions.
- The next concrete action.
