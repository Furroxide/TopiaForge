# Maintainer draft: RC1 credential-exposure closure review

**Tracked maintainer draft — review and decision pending.** Prepared 2026-10-09 for `P0-CRED-01` (blocking), after the user resumed credential-incident closure, for the project owner to forward manually.

Please review the closure of the credential exposure recorded under `P0-CRED-01` for TopiaForge's proposed Windows x64 `0.1.0-rc.1` release, and return an attributable decision within your authorized role. This request records no revocation, rotation, invalidity check or approval. The gate remains blocked pending actual closure evidence.

## Exposure and role coverage

The [blocker register](../../LaunchBlockers.md#p0-blockers) records the exposure. An audit build inherited credential-shaped API and GitHub variables from its parent application, and Xcode's required Flutter scheme pre-action wrote them into a local build log. No value was written to tracked repository files. The two affected workspace DerivedData directories and the affected launcher build-log directory were removed on 2026-07-22. A later build launched from an allowlisted environment produced 13 Xcode activity logs, and a name-only scan of them found no credential-shaped variables or values. The Windows-only, unsigned scope of RC1 does not close this earlier exposure.

This request names no credential, account or log and contains no value. It was prepared from tracked documents only. The affected inventory, incident details and closure evidence stay in restricted private storage with their custodian.

Required role coverage, in the gate's exact order:

| Required role | Requested contribution |
| --- | --- |
| `credential-owner` | Establish the complete private inventory of credentials present in the affected log, revoke or rotate each one, and confirm that the old credentials no longer work. |
| `security-owner` | Review the inventory's completeness, the old-credential invalidity evidence, least privilege for replacements and for local and GitHub secrets, the log retention and deletion disposition, and the sanitized-launch sentinel. |

Please identify every role you cover and your authority or delegation. One person may cover both roles where actually authorized; owning an account does not by itself establish the security-owner role.

## Decisions and evidence requested

Please address each exit criterion in the [blocker register](../../LaunchBlockers.md#p0-blockers) and the [review criteria](ReviewGates.md), recording the evidence privately:

1. **Affected inventory.** The complete private list of every credential present in the affected audit or Xcode log, and how its completeness was established.
2. **Revocation and replacement.** For each item: revoked or rotated, when (UTC) and by whom, and whether a replacement was issued.
3. **Old-credential invalidity.** Evidence that each old credential no longer works, obtained without exposing a value, such as a provider's revocation record or an authenticated failure result.
4. **Least privilege.** A review of any replacement credentials and of the local and GitHub secrets, with anything unneeded removed.
5. **Log retention and deletion.** The disposition of the affected DerivedData, task and build logs under the applicable retention policy, covering the 2026-07-22 removal and any other copies such as backups, synchronized folders or crash reports.
6. **Sanitized-launch sentinel.** Authenticated evidence that a sanitized Xcode launch does not expose credentials: the retained sentinel build record with its scan method, scope and result. State whether the recorded 2026-07-22 name-only scan suffices or what further evidence is required.

## Requested response

Please return a signed or otherwise attributable private decision: **closure approved for the stated scope**, **changes required before approval**, or **cannot approve**, with reasons, conditions, exclusions and unresolved items.

Recommended response fields, usable in your normal private review system:

- Project/release and gate (`TopiaForge 0.1.0-rc.1`, `P0-CRED-01`), and the scope actually reviewed.
- Reviewer identity, role(s), authority/delegation basis and UTC decision time.
- Decision for each numbered item above, with conditions and unresolved items.
- Supporting private references and hashes where available, the attributable approval trail and the record custodian.

These are review-record recommendations, not an enforced attestation schema. No approval or evidence ID is prefilled. Never put a credential value, account identifier, log excerpt or incident detail in chat, tracked files or public release assets; provide safe record references instead.

After actual role coverage and decisions are verified, a maintainer can propose a separately reviewed gate update under the [existing record boundary](ReviewGates.md#review-sequence-and-record-boundary). Unmet conditions remain open. This decision does not approve other gates or authorize candidate construction or publication.

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
