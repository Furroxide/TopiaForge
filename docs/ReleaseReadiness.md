# Release readiness for 0.2.0

The development target is 0.2.0. The [current register](../release/release-readiness.json)
is **blocked**: all five blocking gates remain unmet. This index distinguishes completed
subitems from the evidence still required; it does not supply an approval or qualify an archive.

The register is read from the exact candidate commit at `release/release-readiness.json`.
Historical RC1 records remain in their frozen commits and Git history. Updating the current
register's version does not change those records or transfer RC1 acceptance to 0.2.0.

## Blocking evidence

| Gate | Recorded progress and scope | Evidence still required |
| --- | --- | --- |
| `P0-IP-01` | TopiaForge's own source has an approved AGPL-3.0-or-later licence. The 2026-08-24 web-art disposition accepts that subitem's risk for `0.x`. | The rights basis for naming, injection and compatibility extraction remains unresolved. The web-art disposition does not settle it. See [the IP entry](LaunchBlockers.md#p0-blockers). |
| `P0-OSS-01` | The 2026-08-24 source-inclusion repair and 2026-08-28 asset-coverage audit close engineering gaps. | Compliance review of the LGPL corresponding-source method, OFL/font treatment, notice placement and original terms; then verification of the final BOM, SBOM and archive bytes. See [the licence inventory](ReleaseLicenseInventory.md) and [the OSS entry](LaunchBlockers.md#p0-blockers). |
| `P0-PRIV-01` | Loopback backend tests recorded on 2026-08-28 and 2026-09-08 cover failure and redaction paths. Multiplayer disclosure wording addresses a separate surface. | Authorization for RoboAPI use and approved destination, purpose, consent, authentication, cost, retention, deletion, training and incident-response disclosures, plus remaining native microphone checks. See [Privacy and capabilities](PrivacyAndCapabilities.md). Test results and multiplayer wording do not provide these approvals. |
| `P0-CRED-01` | Affected local logs were removed on 2026-07-22, and a sanitized sentinel build was recorded. | A non-secret completion record for rotation and revocation of every credential exposed in the historical Xcode logs, confirmation that the old values no longer work, and the remaining least-privilege review. See [the credential entry](LaunchBlockers.md#p0-blockers). Never put credential values in evidence. |
| `P0-GAME-01` | The 2026-08-28 build-2409 run is preliminary historical evidence. The 2026-11-15 build-2640 metadata audit is partial compatibility evidence. | Native acceptance on the pinned game build from the frozen candidate, followed by the exact payload and acceptance binding described in [Admin release](AdminRelease.md#private-build-and-exact-candidate-qualification). Earlier runs and metadata checks do not qualify a new candidate. |

The two existing advisory approvals remain unchanged: `P0-TRUST-01`
(`EVID-P0-TRUST-01-0001`) and `P1-SUPPORT-01` (`EVID-P1-SUPPORT-01-0001`),
both recorded on 2026-08-28 for the `0.x` posture. The other advisory gates remain
unmet. Their status does not remove exact-candidate validation requirements.

## Distribution and candidate scope

The 0.2.0 target is settled. As of 2026-11-16, explicit unsigned Windows distribution
is permitted for stable and prerelease `0.x` builds. No certificate may be pinned in
unsigned mode; `1.0` and later still require signing. Windows may show an
unrecognized-publisher or SmartScreen warning. The [release notes](../release/notes/v0.2.0.md)
disclose that the executables are not code-signed. Exact unsigned artifact checks,
signed update metadata and all other release gates remain required.

The four non-game blocking gates must be approved before the release flow reports
`eligible-for-private-build`. Only the game gate may await that candidate's native run.
Final qualification binds the final `main` merge SHA, exact payloads, handoff, detached
decision and acceptance evidence. See [Admin release](AdminRelease.md#private-build-and-exact-candidate-qualification)
and [Release operations](ReleaseOperations.md). No release is qualified by this index.
