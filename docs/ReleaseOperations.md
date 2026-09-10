# Release ownership and incident operations

The interim owner for the first TopiaForge release line is repository administrator `@furroxide`. Before
`0.1.0-rc.1` can be published, that account must confirm that GitHub notifications and private vulnerability reports
are monitored and name a delegate for any role it cannot cover.

| Responsibility | Intake and authority | First-RC expectation |
| --- | --- | --- |
| Public support | GitHub issues; `@furroxide` triages or delegates | Best effort; no response-time or LTS promise. |
| Security intake | GitHub private vulnerability reporting; `@furroxide` coordinates | Keep reports private until a fix/advisory window is agreed. |
| Release manager and notes | `@furroxide` | Reviews the exact candidate evidence and records the final ship/no-ship decision. |
| Release incident commander | `@furroxide` or a named delegate | Pauses publication and coordinates advisory, replacement, and user communication. |
| Package trust, revocation, and takedown | `@furroxide` with security/product review | Records affected identities/hashes and the recovery path before changing indexes. |
| Rollback | Release manager | There is no in-place rollback for the initial immutable release; ship a new version or advisory. |

## Administrator-orchestrated release

Production bytes are created only on administrator-controlled machines. RC1 is Windows x64 only;
Linux/Proton acceptance is unavailable until a reviewed native isolation implementation and its exact-candidate
evidence path exist. The retired runner cannot be restored by a policy change; see the
[future Linux prerequisites](AdminRelease.md#future-linux-acceptance). Any future same-host evidence must disclose
that it is non-independent. The Windows workstation drives
`release-admin.ps1`. Its two canonical ecosystem builds must be byte-identical before packaging.

Private preparation requires the four non-game blocking approvals in the frozen twelve-gate register and the
policy-approved final `main` merge SHA. Only the game gate may await the exact candidate. Run the mandatory matrix
in an isolated Windows user/session or VM that isolates Unity's persistent data and never accesses the normal
user's data. A separate BepInEx profile alone is insufficient. Acceptance requires all 36 redesign cases,
all 15 SDK cases, 10 game lifecycle cycles, and 16 Unity authoring cycles from the tracked inventories in
[`LiveGameAcceptance.md`](LiveGameAcceptance.md). Historical smoke logs or a build pass cannot replace those cases.

The durable sequence is `preflight → platforms-built → built → accepted → staged → dispatch-requested → published`.
A successful `build` seals the tested payloads and `release-handoff-v1` at `built`. Prepare and review
`release-candidate-readiness-v1.json` and `release-candidate-acceptance-v1.json` in the candidate assets directory;
`qualify` validates their exact source, contract, payload and evidence hashes and atomically records `accepted`.
Stage, dispatch and resume revalidate that assessment. Accepted bytes cannot be rebuilt or repacked, and a rehearsal
can never qualify or publish. See [`AdminRelease.md`](AdminRelease.md) for commands and record requirements.

Unsigned Windows RC1 is authorized and recorded in the release policy. The construction repairs are implemented
and synthetic regressions pass; see the revision-specific
[verification record](internal/gamemode-contract/Status.md). The exact candidate build and isolated acceptance remain
pending; no candidate is qualified. In unsigned mode, all three Windows executables must be verified unsigned, and the
handoff CMS asset and its decision digest must be absent. In signed mode, the launcher, CLI and GameCompat extractor
require Authenticode signatures and RFC 3161 timestamps from the exact pinned certificate; the detached handoff CMS
must also pass its certificate and timestamp checks. A missing credential never selects unsigned mode.
Ed25519 update signing remains mandatory in either mode, along with exact-byte qualification and protected approval.

Public handoff metadata contains only scrubbed validation summaries and evidence digests. Raw game logs,
credentials, usernames, hostnames, local paths and run-specific timestamps stay off GitHub. Machine checks establish
evidence binding; the release approver must verify the actual reviewers' identity, authority and records.

The administrator stages an exact matching draft and dispatches the GitHub finalizer only after qualification has
passed and the signed annotated tag has been pushed. Approval of the protected `release` environment is the last
human checkpoint. GitHub verifies the administrator-built bytes, creates the update signature and custom verification
attestation, rechecks the complete asset inventory, and publishes automatically. A rerun may only verify identical
state. Release authorship and all locally staged asset uploaders are pinned to `furroxide` actor ID `221987073`;
workflow-generated public metadata is limited to the stable `github-actions[bot]` identity and GitHub Actions
integration. Any identity or asset-classification mismatch fails before publication.

The publication workflow is globally serialized and re-fetches the exact draft and asset inventory immediately
before its single publication transition. GitHub's release-update API has no documented conditional unsafe `PATCH`,
so no administrator may manually mutate an approved draft while the finalizer runs.

## Incident procedure

1. Stop the local orchestrator before dispatch, or reject the protected-environment approval before publication, and
   preserve the affected tag, artifact hashes, logs, and attestations. Never replace an immutable asset or move/delete
   a protected version tag.
2. Classify impact across the loader, launcher, SDK, mods, VPM packages, registries, game compatibility, credentials,
   and user data. Rotate exposed credentials immediately through their owning provider.
3. If an unpublished candidate is affected, keep it blocked and cut a new RC version after remediation. If a public
   release is affected, publish a GitHub advisory and a new immutable version. Never silently rewrite registry history.
   Note the current limitation: there is no package revocation mechanism, so an affected package cannot be marked as
   withdrawn and installed clients cannot be notified through the launcher — the advisory and the replacement version
   are the only available signals. See `P0-TRUST-01`; revise this step once revocation ships.
4. Give installed users concrete safe-mode, disable, uninstall, or repair steps and identify whether saves or synced
   multiplayer state are affected.
5. Attach the timeline, decision owner, evidence, and follow-up work to the release record. For a legal, privacy, or
   security incident the owner records the applicable determination before closure; where an independent approver for
   that area exists, obtain their sign-off first.

Support ownership is recorded as approved for the `0.x` line in the
[tracked readiness register](../release/release-readiness.json). Before publication the release manager confirms
continued monitoring or delegation and completion of the remaining blocking reviews in
[`LaunchBlockers.md`](LaunchBlockers.md).
