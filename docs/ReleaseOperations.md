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

Production bytes are created only on administrator-controlled machines. The Windows workstation drives
`release-admin.ps1`; it builds Windows and runs exact Unity/Robotopia acceptance, invokes Ubuntu 24.04 through WSL2
for Linux x64, and runs the RC1 Proton acceptance journey in that same WSL2/WSLg environment. The canonical ecosystem
payload is built twice and must be byte-identical before the same bytes are distributed to both platform builds.

The release manager supplies a local Windows Creator-workbench evidence bundle; the orchestrator itself produces the
same-host Proton evidence from the exact Linux archive. This RC1 evidence is explicitly non-independent and identifies
WSL2/WSLg plus the pinned Proton runtime. Public handoff metadata contains only scrubbed validation summaries and
evidence digests; raw Robotopia logs, credentials, usernames, hostnames, local paths, and timestamps stay off GitHub.
RC1's reviewed policy requires the launcher, CLI, and GameCompat extractor to
be Authenticode-signed and RFC 3161 timestamped by the exact pinned
certificate. The administrator also signs the exact aggregate handoff bytes
with a detached CMS signature that GitHub verifies before opening or trusting
the staged platform archives.

The administrator stages an exact matching draft and dispatches the GitHub finalizer only after local validation has
passed and the signed annotated tag has been pushed. Approval of the protected `release` environment is the last human
checkpoint. GitHub then verifies rather than builds, creates the update signature and custom verification attestation,
rechecks the complete asset inventory, and publishes automatically. A rerun may only verify identical state.
Release authorship and all locally staged asset uploaders are pinned to
`furroxide` actor ID `221987073`; workflow-generated public metadata is instead
limited to the stable `github-actions[bot]` identity and GitHub Actions
integration. Any identity/classification mismatch is a release incident and
fails before publication.
The publication workflow is globally serialized and re-fetches the exact draft
and asset inventory immediately before its single publication transition.
GitHub's release-update API has no documented conditional unsafe `PATCH`, so no
administrator may manually mutate an approved draft while the finalizer runs.

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

The first-RC support gate remains open until the owner confirms monitoring and the legal/privacy/trust policies in
`LaunchBlockers.md` are approved.
