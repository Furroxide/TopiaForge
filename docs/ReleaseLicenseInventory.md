# TopiaForge release license inventory

Status: **owned-surface and redistribution audit complete** for
`1.0.0-rc.1`.

TopiaForge-owned code and content are licensed under the GNU Affero General
Public License, version 3 or later (`AGPL-3.0-or-later`), with
`Copyright (C) 2026 furroxide`. Contributions made after the
`v1.0.0-rc.1` cutover use Developer Certificate of Origin 1.1 sign-off;
inbound terms match outbound terms. Existing history is grandfathered.
Third-party materials retain their original licenses and are not relicensed by
the project AGPL grant.

This supersedes the earlier MIT declaration.

## Owned release surfaces

| Surface | Declaration and placement |
| --- | --- |
| Repository and platform archives | Root `LICENSE`; release packaging copies `LICENSE` and `DCO` beside the product payload. |
| Sixteen first-party mods | SPDX `AGPL-3.0-or-later` and package-relative `LICENSE`; the packer injects the reviewed shared mod license into every first-party archive. |
| Twelve SDK NuGet packages | `PackageLicenseExpression` is `AGPL-3.0-or-later` through the shared pack policy or an equivalent project declaration. |
| VPM resolver, world companion, and UGC companion | SPDX `AGPL-3.0-or-later` in each `package.json` with the complete license text in the package directory. |
| Flutter launcher UI package | Complete license text in `packages/launcher_ui/LICENSE`. |
| CLI, launcher domain/data, sidecar, website, templates, samples, and repository tooling | Covered by the root license unless a more specific adjacent third-party notice applies. |

The SDK packages are licensed on the same terms as the rest of the project, with
no linking exception. A mod distributed against the TopiaForge SDK is therefore
a derivative work and must itself be licensed `AGPL-3.0-or-later`.

Author-generated mod and Unity-package scaffolds default to
`AGPL-3.0-or-later` and receive the full license text, matching the terms that
linking the SDK requires. An author who supplies an explicit `NOASSERTION`
still gets the no-grant notice and a non-publishable project. Test-only SPDX
fixtures and SPDX SBOM `NOASSERTION` values for unknown third-party conclusions
are not project-license placeholders.

## Third-party redistribution audit

The release payload retains the exact upstream licenses and notices recorded in
`THIRD_PARTY_NOTICES.md` and the machine-readable provenance under
`third_party/`. Release validation verifies:

- pinned BepInEx, Harmony, MonoMod, Cecil, and UnityDoorstop inputs, license
  files, notices, source references, versions, and hashes;
- .NET runtime, MetadataLoadContext, Metadata, and Immutable license/notice
  bundles from the exact restored packages;
- Flutter/Dart generated notices and the standalone CLI license bundle;
- Node and Dart dependency lockfile inventories;
- SPDX License List Data provenance; and
- redistributed font and artwork notices.

The audit does not change any third-party license. Archive inspection, BOM,
SBOM, notice, provenance, and deterministic-package tests remain mandatory on
the exact release SHA. The release catalog is `ready` only while those strict
checks pass.

## Contribution policy

The canonical DCO 1.1 text is checked in as `DCO`; `CONTRIBUTING.md` explains
the required `Signed-off-by` trailer. The PR policy begins enforcing trailers
for commits introduced after the immutable `v1.0.0-rc.1` cutover tag. The
repository web-commit sign-off setting is enabled at that cutover so GitHub
authored commits can carry the same certification.
