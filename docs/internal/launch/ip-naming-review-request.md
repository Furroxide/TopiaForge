# Maintainer draft: RC1 IP, naming and retained-asset review

**Tracked maintainer draft — review and decision pending.** Prepared 2026-09-09 for `P0-IP-01` (blocking).

Please review the rights basis and public naming for TopiaForge's proposed Windows x64 `0.1.0-rc.1` release, and return an attributable decision within your authorized role. This request records no approval. The gate remains blocked pending actual review evidence.

## Review scope and role coverage

The baseline supplied for review is release source **`437733854795c11e684eac9d59d6bc52ada9516e`**, tree **`976326d02fd68b19a2e10bde923e15510df441a6`**. The [reviewed-source and automated-verification summary](Evidence.md#reviewed-source-and-automated-verification) identifies that reviewed source. This is the reviewed release-branch source, not a frozen final `main` candidate. Identify any later source changes that require renewed review; final candidate archive contents and notices must be rechecked after construction.

Required role coverage, in the gate's exact order:

| Required role | Requested contribution |
| --- | --- |
| `ip-trademark-counsel` | Review the rights/naming basis, non-affiliation wording and retained-material dispositions. |
| `project-owner` | Confirm the intended distribution/use scope, asset provenance and implementation of conditions. |
| `robotopia-owner` | Confirm the authority or owner disposition applicable to Robotopia integration, names and retained material. |

Please identify every role you cover and your authority or delegation. One person may cover multiple roles where actually authorized; project ownership alone does not establish the other roles.

Please address all of the following, identifying written authority or an approved clean-room/non-affiliation basis, scope limitations and any necessary changes:

- **Robotopia and TopiaForge naming:** public names, affiliation claims, canonical full/short notices and required placement.
- **Robotopia injection and compatibility extraction/baselines:** the proposed integration and clean-room basis.
- **Registry claims:** the names and representations made about compatibility, source, ownership or affiliation.
- **Web-derived art and adapted icons:** item-specific rights, provenance and any retention, removal or replacement instructions.
- **Fonts and custom-world content:** the rights basis and applicable retained-scope limitations. Separate OSS compliance approval remains under `P0-OSS-01`.
- **Retained-material record:** for each retained item, identify its provenance, transformation, hash, license/authority and approver; flag missing information rather than assuming a grant.

## Existing material and limitations to resolve

The [canonical trademark wording](../../../TRADEMARKS.md) has not been reviewed by counsel. The source documentation describes the full notice in README, SUPPORT and release notices; it explicitly does not claim that launcher or in-game notice placement is implemented. Please specify any wording or placement changes required.

The three Robotopia web images formerly bundled by the launcher were **replaced on 2026-09-09** and are no longer present in the repository. These paths now hold CC0 artwork by GrafxKid obtained from OpenGameArt:

- `packages/launcher_ui/assets/brand/topiaforge-city-header.webp`
- `packages/launcher_ui/assets/brand/baby-stitch.webp`
- `packages/launcher_ui/assets/brand/sheriff.webp`

The [third-party notices](../../../THIRD_PARTY_NOTICES.md) record, for each file, the OpenGameArt source URL, the upstream and installed SHA-256 values, and the exact crop and background-keying transformations applied. CC0 1.0 is a public-domain dedication carrying no attribution requirement and no share-alike term, so these files raise no compatibility question against TopiaForge's AGPL-3.0-or-later grant and need no redistribution grant from any third party.

This supersedes the 2026-08-24 project-owner disposition, which had accepted retaining the Robotopia images with attribution as a non-blocking risk for the `0.x` line pending a written grant. That disposition is moot: there is nothing left to retain, and nothing to revisit before `1.0` for these three files. **No counsel decision is sought on web-derived art.** The sub-item is included here only so the record shows how it was closed.

The remaining questions in this request — the Robotopia and TopiaForge names, Robotopia injection, compatibility extraction and baselines, registry claims, adapted icons, fonts, and custom-world content — are unchanged and still need counsel.

Please consult the [exact IP gate criteria](../../../docs/LaunchBlockers.md#p0-blockers) and the [review criteria](ReviewGates.md#review-sequence-and-record-boundary). The retained [notice audit](Evidence.md#retained-source-audits) and [15 regression-test results](Evidence.md#retained-source-audits) support notice consistency only; they establish neither rights nor reviewer approval.

## Requested response

Choose a decision for your reviewed scope and explain it:

- **Approve the stated scope**, with explicit conditions, exclusions, expiry/re-review triggers and any conditions that must be satisfied before approval takes effect.
- **Changes needed before approval**, listing each required removal, replacement, wording change, missing record or additional review.
- **Cannot approve**, with the reason and any further authority or information needed.

Recommended response fields, usable in your normal signed letter, email or private review system:

- Project/release and gate (`TopiaForge 0.1.0-rc.1`, `P0-IP-01`); exact source/tree and documents or assets actually reviewed.
- Reviewer identity, role(s), authority/delegation basis and UTC decision time.
- Decision, reasoning, covered scope, conditions/exclusions and unresolved items.
- Supporting record references and hashes where available; item-specific retention/removal/replacement instructions.
- Durable attributable response/reference and the private record custodian.

These are review-record recommendations, not a newly required JSON schema. Please return the decision through the agreed private channel; no signature, approval identifier or legal conclusion has been prefilled. Once actual role coverage and decisions are verified, the maintainer can propose a separately reviewed gate update with opaque references. Unmet conditions must be resolved before marking the gate approved.

Please keep reviewer details, internal legal correspondence and private evidence out of public release assets. This IP decision does not approve the other release gates, authorize a candidate build or publication, or certify unbuilt archive bytes.

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
