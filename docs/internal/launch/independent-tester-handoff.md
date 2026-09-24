# Maintainer draft: independent RC1 tester handoff

Prepared 2026-09-09 for the nested independent player/author journeys under advisory `P1-E2E-01`. This tracked maintainer draft is a brief for later use, not evidence of testing or permission to distribute, install, launch, create accounts or publish a registry. No candidate or tester identities are supplied yet. Neutral build resources remain a plan; credential-incident closure is user-deferred and blocks private candidate build.

## Before the testers start

The release owner must supply:

- A reviewed, qualified future Windows x64 `0.1.0-rc.1` payload through an authorized private handoff, with its exact filename, size, SHA-256, final-main source SHA and qualification/handoff reference. Supply the canonical package inventory and matching user documentation. There is no download link or hash to fill in from an old archive. Changed bound bytes require new candidate evidence; testers must not rebuild or repack TopiaForge.
- Named independent participants covering `external-player-reviewer` and `external-author-reviewer`, plus the `release-owner` reviewer. Record prior involvement, clean-host state and any assistance privately. Reusing the maintainer's same-host QA run does not establish independent clean-machine evidence.
- An authorized Windows host and separately initialized QA user/session or VM for each journey, a clean licensed Robotopia build-2409 QA installation, isolated persistent data and approved writable roots. The [isolated QA setup plan](isolated-qa-setup-plan.md) describes the boundaries. No personal saves, authentication stores or maintainer credentials are imported.
- Player prerequisites from the release documentation; author prerequisites additionally include the pinned .NET SDK `10.0.301`. The author receives the release archive and user documentation, without a TopiaForge source checkout or Unity installation. Supply the corresponding published documentation or a reviewed candidate documentation snapshot, not internal development instructions.
- A private evidence submission channel and retention owner, the exact reversible failure/repair fixtures and recovery instructions, and any separately approved update pair. For the registry portion, provide an authorized static HTTPS test host/namespace, nonproduction package scope and publishing permission. Keep access secrets out of the brief and reports. If hosting or a real update pair is unavailable, record that subcase as pending.

These are start conditions, not additional setup work authorized by this handoff. Current policy is Windows x64 only; RC1 is unsigned and has no prior product rollback target. [AdminRelease](../../../docs/AdminRelease.md) governs candidate identity; [the release policy](../../../release/release-policy.json) governs supported artifacts.

## Player journey

Use the supplied player documentation first. Record unclear wording, missing information, wrong paths and every point where help is needed before accepting maintainer guidance.

| Step | Observation to retain |
| --- | --- |
| Obtain and extract the exact archive; follow [README player instructions](../../../README.md#for-players), including `topiaforge doctor --strict` | Archive hash agrees; intact portable layout; no developer tools required; diagnostics and unsigned-distribution messaging are understandable. |
| Discover/select the approved QA game installation and install/repair the runtime | Selected path is the authorized isolated installation; BepInEx/runtime state becomes ready. Repeat the approved repair fixture and show recovery without losing QA profile state. |
| Install the supplied canonical package set | Source, dependency plan, aggregate capabilities and package integrity are visible before confirmation. Profile selection and enable/disable behavior are understandable. |
| Launch normally, exit, then use Safe Mode | Confirm the actual expected game/session, manager access and package state. Safe Mode reaches main-menu with no packages loaded. A process starting alone is not success. |
| Exercise one approved failure and diagnose it | Preserve the first stable diagnostic code, expected versus observed behavior, docs followed and recovery steps. Do not damage personal/game files or improvise crashes; request a bounded fixture if none was supplied. |
| Recover, then perform the supplied manual-update/fallback scenario | Show a successful clean retry and preserved intended QA state. A genuine update/rollback needs an approved exact before/after pair. Re-extracting RC1 proves reinstall/fallback only; report unavailable upgrade/rollback as pending. |

Use [Diagnostics](../../../docs/Diagnostics.md) for error interpretation. [LauncherUpdates](../../../docs/LauncherUpdates.md) describes whole-package updates, but its future upgrade exercises do not create an available prior release or authorize production update tests. Record any documentation/policy contradiction for correction.

## Independent safe-code author journey

Work in a new project directory outside the extracted release. Use only the supplied user documentation and normal product help. The [release checklist](../../../docs/ReleaseChecklist.md#7-exact-unity-validation) promises creating **and launching** a working safe code mod in at most five commands, with no checkout or Unity. Keep a command count, including `cd` and remediation commands; report prerequisite setup separately without hiding extra commands needed to make the mod work.

The verified [first-mod walkthrough](../../../docs/YourFirstMod.md) contains these four commands. Use an interactive terminal, where `dev` launches and tails by default; redirected output otherwise stops after install. Replace the example author with the tester's intended attribution and use the licence the tester chooses explicitly.

```sh
topiaforge doctor --strict
topiaforge new mod example.first-mod --name "First Mod" --author "You" --license AGPL-3.0-or-later --version 1.0.0
cd example.first-mod
topiaforge dev
```

Confirm the detected game path is the approved QA installation before allowing launch. If the documented override or another remediation is necessary, retain it in the record. The [development-loop guide](../../../docs/CliDevLoop.md) documents `--game-dir`, `--launch` and session confirmation; do not silently substitute hidden setup. Confirm restore, build, generated tests, packing and validation succeeded, then observe the mod loaded in F10 and exercise its documented `example.first-mod:greet` behavior. Retain package hash and attributable result privately. Command exit or process creation alone does not establish a working mod.

After that first-run measurement:

1. Change the greeting, add a test assertion and repeat the documented development loop; observe the required restart and changed behavior. Record first-failure diagnostics and the quality of their remediation.
2. Follow [Publish a mod](../../../docs/PublishingYourMod.md): validate the project to zero findings, pack, validate the actual generated archive, and retain its hash/size. Keep explicit author/licence metadata and publishable notices. The guide's example package filenames must be replaced with the real generated id/version.
3. If the approved HTTPS test context exists, follow the documented registry index/validation workflow and host immutable package bytes plus the index there. Add that exact source under Settings → Package Sources, review dependencies/capabilities, install through the launcher and confirm behavior. Official community submissions remain closed. No production namespace, unrelated service or credential use is implied.
4. Use a new package version for an update, retain previous bytes/history, rebuild/validate the index and observe the launcher update. Use only approved bounded fixtures for hash rejection, failed download and recovery. A local package folder/index can exercise local install and update when hosting is absent, but it does not pass the self-hosted HTTPS publication subcase.

The publication/update workflow is additional coverage, not part of the five-command first-mod limit. If a documented command is unavailable in the packaged release or requires an undocumented checkout, stop that subcase and report the gap instead of reconstructing the tool from source. [RegistryFormat](../../../docs/RegistryFormat.md) defines the source shapes and immutable version history.

## Evidence and review

Submit a private record per journey: tester role and independence statement, candidate source/archive hashes, documentation version, authorized host/isolation reference, command count, numbered actions, expected/observed outcomes, first diagnostic code, assistance received, and evidence references. Classify each subcase as **not started**, **blocked**, **observed pass** or **observed failure**; never infer success from an omitted step. Retain usability stuck points, time to first success and doc clarity even when technical checks pass. Declare all maintainer intervention and distinguish a coached retry from the original independent attempt.

Use the approved private channel for bounded, reviewed evidence. Do not place credentials, authentication values, voice/dialogue transcripts, personal paths or raw logs in public issues. Public summaries carry redacted findings and safe reviewed evidence references/digests only. Both external reviewers and the release owner must review the actual results before any `P1-E2E-01` decision or dated scope-limited disposition is recorded. The gate remains pending. Use the separate [native UX/accessibility operator handoff](native-ux-accessibility-handoff.md) for that coverage; neither prepared brief supplies executed acceptance.

Maintainer references: [P1-E2E-01 exit criteria](../../../docs/LaunchBlockers.md), [tracked reviewer roles](../../../release/release-readiness.json), [NextActions](NextActions.md).

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
