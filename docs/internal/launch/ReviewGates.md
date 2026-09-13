# Non-game review gates and record boundary

Updated 2026-09-09. The [tracked readiness register](../../../release/release-readiness.json) still blocks all four gates below with empty `evidenceIds`. The three prepared review requests do not supply approvals; credential-incident closure was explicitly deferred. Only GAME may remain deferred at private candidate construction.

| Gate | Required roles, in contract order | Actual decision needed |
| --- | --- | --- |
| `P0-IP-01` | `ip-trademark-counsel`, `project-owner`, `robotopia-owner` | Authority or approved clean-room/non-affiliation basis covering names, injection, compatibility extraction/baselines, registry claims, retained art/icons/fonts/world content and limitations |
| `P0-OSS-01` | `ip-trademark-counsel`, `release-owner` | Approved retained redistribution scope, original terms/notices, LGPL source-delivery method and OFL derivative handling; later exact archive recheck |
| `P0-PRIV-01` | `backend-owner`, `privacy-legal`, `product-owner`, `robotopia-owner`, `security-owner` | Endpoint/token authorization; authoritative retention/deletion/training/geography/account/cost policy; consent/disclosures/off controls and acceptance-matrix review |
| `P0-CRED-01` | `credential-owner`, `security-owner` | Complete private affected inventory, revocation/replacement and old-credential invalidity, least privilege and log retention/deletion disposition, authenticated sanitized-sentinel evidence |

Use the [IP request](ip-naming-review-request.md), [OSS request](oss-redistribution-review-request.md) and [privacy request](privacy-backend-review-request.md). The existing three-image 0.x disposition covers that asset sub-item only. Source audits establish inventory or behavior; they do not establish legal authority, backend permission or actual credential revocation. The Windows-only/unsigned choice does not close earlier exposure. [LaunchBlockers](../../LaunchBlockers.md) retains detailed history; [Evidence](Evidence.md) describes the available engineering record.

## Review sequence and record boundary

1. Retrieve actual existing records from their owners or obtain new reviews. Request preparation and a syntactically valid evidence ID cannot prove that a record exists.
2. Confirm every listed role is covered by an actually authorized reviewer. One person may cover multiple roles if authorized; ownership does not automatically grant other roles. Do not invent a fixed number of people.
3. Retain the signed or otherwise attributable decision privately, including reviewed scope, conditions and unresolved items. Use only opaque lookup references in tracked gate changes.
4. Resolve conditions before approval, prepare a separately reviewed gate change and integrate it normally before freezing the final `main` SHA. Validate the exact committed gate/schema through `release validate-prerequisites --version 0.1.0-rc.1 --target-sha <final-main-sha>`.
5. After candidate construction, perform any required review of the actual BOM/SBOM, notices, corresponding source and archive bytes. A source-scope review cannot inspect unbuilt artifacts.

Recommended private record fields are project/release, gate, exact reviewed source/artifact scope, decision and conditions, reviewer identity and every covered role with authority/delegation basis, UTC time, evidence references/hashes, unresolved items, durable approval trail and custodian. These are recommendations, not a new enforced attestation-file schema. Keep internal legal/privacy review, identities, credential inventory and incident details out of public artifacts and Git.

## Machine contract

The register uses schema version 1, twelve ordered gates and `candidateBinding.mode=git-blob-at-target-sha`. The [schema](../../../schemas/topiaforge.release-readiness-v1.schema.json) closes properties; [gate contracts](../../../apps/topiaforge_cli/lib/src/release_readiness_gate_contracts.dart) pin identities, priorities, enforcement and reviewer-role order. All four gates above are P0/blocking; accepted risk is unavailable. Preserve all seven advisory rows and their actual status.

Blocked rows retain their required reason and no evidence IDs. Approved rows require sorted unique IDs with the gate's `EVID-<gate-id>-` prefix and four-digit suffix, and omit blocked reasons/accepted-risk fields. No actual approval ID is assigned by this document. The overall register remains blocked while GAME is blocked.

There is no separately enforced non-game attestation-file schema. The candidate-acceptance `reviewerEvidence` array (`evidenceId`, `role`, `reference`, `sha256`) applies to GAME only. The private QA provisioning record instead has a nonempty reviewer-reference string. Do not mix those contracts or invent non-game approvals inside GAME evidence. Actual identity and authority verification belongs to the authorized reviewers and protected approver.
