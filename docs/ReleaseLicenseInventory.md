# TopiaForge release license inventory

Status: **publication blocked** for `1.0.0-rc.1`.

This inventory records the repository's current declarations; it does not select, grant, or reinterpret a license.
The release catalog must remain `blocked` until the project owner and qualified legal reviewer approve the decisions
below and release engineering verifies the resulting candidate artifacts.

## Current owned-package declarations

| Surface | Current declaration | Release consequence |
| --- | --- | --- |
| Repository root | No root `LICENSE` | No project-wide outbound grant is declared. |
| Release policy | `OWNER_DECISION_REQUIRED`, `licenseFile: null`, `decisionStatus: blocked` | Strict release validation must fail. |
| Sixteen first-party mod manifests | `NOASSERTION` | The `.topiaforgemod` packages are not publishable. |
| VPM resolver, world companion, and UGC companion | `NOASSERTION` plus a no-grant notice | The three VPM packages are not publishable. |
| Twelve packable SDK NuGet projects | The shared pack target defaults to `MIT`; four projects repeat `MIT` explicitly | This conflicts with the unresolved project-wide decision and must not be treated as approval. |
| Launcher UI package | A no-grant placeholder file | The placeholder is not a redistributable project license. |
| Contributor policy | No inbound contribution terms selected | Substantive external contributions remain blocked pending owner review. |

Dependency license data in generated lockfiles describes third-party dependencies, not TopiaForge-owned code. The
third-party inventory and required redistributed notices remain in `THIRD_PARTY_NOTICES.md` and adjacent license files.

## Decisions required from owner/legal

- Identify the copyright holder or holders and approve the outbound SPDX expression for each independently
  distributed TopiaForge surface.
- Approve inbound contribution terms and any contributor attestation or agreement required before accepting
  substantive external contributions.
- Approve Robotopia name, compatibility, injection, brand, artwork, font, template, and sample use for the public RC.
- Approve the exact third-party BOM and notice placement, including the UnityDoorstop LGPL corresponding-source
  delivery method and every bundled BepInEx/Harmony/MonoMod/Cecil/.NET/Flutter/Dart/Node/SPDX asset.
- Approve privacy, backend authorization, package trust, revocation, takedown, and installed-user recovery policy.

## Propagation and evidence after approval

- Add the canonical root license and record its SPDX expression and path in `release/release-policy.json`.
- Replace every owned `NOASSERTION`, no-grant placeholder, shared NuGet default, and explicit NuGet expression with
  the approved per-package mapping; do not infer that every surface uses the same expression.
- Update author templates so generated projects remain non-publishable until an author explicitly selects terms.
- Rebuild all SDK, mod, VPM, launcher, sidecar, BOM, and SPDX SBOM outputs from the frozen candidate SHA.
- Inspect every final archive for the approved root/package licenses, third-party notices, corresponding-source
  references, SPDX relationships, and byte-identical nested ecosystem payload.
- Attach the owner/legal approval record and final artifact inspection to `P0-LIC-01`, `P0-IP-01`, and `P0-OSS-01`.

Until every item above is complete, the repository must not set the catalog to `ready`, create the protected version
tag, upload public release assets, or publish the GitHub prerelease.
