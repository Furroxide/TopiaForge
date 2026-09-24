# Maintainer draft: third-party redistribution review

**Status: tracked maintainer draft; review and decision pending.** Prepared for the project owner to forward manually. No reviewer decision or gate evidence has been supplied by this request.

**To:** authorized reviewers covering `ip-trademark-counsel` and `release-owner`.

Please review the retained third-party inventory and proposed redistribution method for TopiaForge `0.1.0-rc.1`, and provide an attributable decision for **P0-OSS-01**. The requested release scope is Windows x64, its thirteen released mods, and the two VPM packages embedded in the ecosystem. Repository coverage includes additional platform sources; their presence does not request approval to release those platforms.

The source under review is commit `79740dc1d747e75510ca477e7be0d5c0b109a085`, tree `10b55514193727d205fe84cb62e8e03d9aa4fb90`. This is the release branch after the 2026-09-24 stabilization, which retargeted RC1 to Robotopia build 2478 ([evidence](Evidence.md#build-2478-retarget-2026-09-24)). It supersedes the earlier baseline `4377338`. This is a source review before candidate construction. The final integrated `main` SHA and actual candidate BOM, SBOM, notices, and archive bytes must be checked later; no future archive is represented as already inspected.

Please address the following points and identify any unsupported item by its repository or intended archive path:

1. **Retained inventory and original terms.** Review the [release license inventory](../../../docs/ReleaseLicenseInventory.md) and [third-party notices](../../../THIRD_PARTY_NOTICES.md), including BepInEx/Harmony/MonoMod/Cecil, UnityDoorstop, .NET components, Flutter/Dart dependencies, SPDX data, fonts, artwork, and Unity TextMesh Pro resources. Confirm the applicable original terms, source/provenance, notices, and redistribution conditions for each retained component. Assess the boundary between TopiaForge's AGPL-3.0-or-later grant and third-party grants; third-party material is not relicensed by the project license. The [catalog](../../../release/catalog.json) defines the proposed payload inventory.

2. **LGPL corresponding-source delivery.** Assess the proposed UnityDoorstop delivery method and any additional obligations needed for this distribution. [BepInEx provenance](../../../third_party/BepInEx/provenance.json) pins BepInEx `5.4.23.5` and UnityDoorstop `4.5.0`, commit `33dab9a6733862eb81869ff08431d9478b28784b`. It declares `UnityDoorstop-4.5.0-source-33dab9a6733862eb81869ff08431d9478b28784b.zip`, 114,921 bytes, SHA-256 `a71789ffcb359f6a37c6d2c7bbdd0527231e9db00bf46637693cff88ba7ffb80`, alongside the [LGPL-2.1 text](../../../third_party/BepInEx/LICENSES/UnityDoorstop-LGPL-2.1.txt). Packaging is documented to copy that source archive into release payloads and reject missing declared source. Please verify whether the supplied source, build material, delivery placement, and notices are sufficient, and specify any correction. An upstream URL or source metadata alone does not establish what a future recipient receives.

3. **OFL fonts and derived assets.** Review Quicksand, Audiowide, and Liberation Sans, their original OFL notices, normalized filenames, and generated glyph/font assets inside the Unity UI bundle. Assess derivative naming or reserved-name treatment and notice propagation into compiled bundles and distributed source/tooling. The notices record replacement of the earlier web-derived Quicksand copy with upstream bytes and addition of Liberation's notice. Review the separate Unity Companion License treatment of TextMesh Pro resources. EmojiOne was removed; confirm the intended release does not reintroduce it.

4. **Notice placement and inventory coverage.** Review the root/platform license and DCO placement, first-party mod and VPM package licenses, SDK package declarations, BepInEx license/source delivery, generated Flutter `NOTICES.Z`, CLI dependency license bundle, and exact .NET license/notice bundles. The [blocker entry](../../../docs/LaunchBlockers.md#p0-blockers) describes the historical allowlist gap and subsequent exhaustive source asset audit. Audit passes demonstrate mechanical coverage only. Please identify missing notices, incompatible or unsubstantiated terms, and any remove/replace or packaging instructions.

5. **Replaced art and review boundaries.** The three Robotopia web rasters the launcher formerly bundled were replaced on 2026-09-09 with CC0 artwork by GrafxKid from OpenGameArt, under the same filenames. The notices record the source URLs, hashes and transformations. Please confirm that no Robotopia web asset remains in the proposed payload, and that the CC0 replacements need no further notice. Names, integration and other rights questions stay with [P0-IP-01](ReviewGates.md).

Please return a signed or otherwise attributable private record. Suggested response fields below are a review aid, **not an enforced attestation schema**:

- Project/release, gate `P0-OSS-01`, exact reviewed commit/tree and material scope.
- Decision: approved scope, changes required, declined, or pending; conditions, exclusions, unresolved questions, and precise corrective instructions.
- Reviewer identity and role(s), with authority or delegation basis covering both required roles. Policy does not require two different people, but each role needs actual authorization.
- Decision time in UTC, evidence references and hashes where applicable, approval trail, and the record's custodian/location.
- Required final-candidate recheck: final source SHA, BOM/SBOM completeness, original license/notice placement, corresponding-source delivery, deterministic nested payloads, and actual archive contents. State what would invalidate or require renewal of this source-scope decision.

Keep the substantive review privately with its custodian. A maintainer can prepare a separately reviewed gate update only after verifying the real record and role coverage. No evidence ID has been assigned here. See the [review record boundary](ReviewGates.md#review-sequence-and-record-boundary) and [tracked gate contract](../../../release/release-readiness.json).

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
