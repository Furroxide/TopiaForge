# Compatibility and Versioning Policy

## Version lines

QuantumWorks runtime, SDK, launcher packages, CLI, schemas, and first-party mods use Semantic Versioning 2.0. Before
`1.0.0`, a minor release may contain an intentional breaking API or contract change; patch releases must remain
backward-compatible within the same minor line. Build metadata does not affect precedence.

The runtime and launcher must reject malformed versions and unsupported schema versions explicitly. They must not guess
an invalid version, silently select an arbitrary package, or reinterpret `x` inside prerelease/build metadata as a
wildcard. Dependency planning compares full SemVer precedence, including prerelease identifiers.

## Manifests and serialized data

`robotopia.mod.json` schema version 2 is the current package contract. Readers ignore unknown fields so a newer producer
can add optional data; authoring tools preserve unknown manifest fields when they read and rewrite a manifest. A reader
must fail closed when a required field is missing, a known field has the wrong type, or the declared schema version is
unsupported.

Persistent launcher/manager state is backward-readable within a release line. Writes are bounded and atomic, keep a
last-known-good backup where recovery is supported, and never use a package-supplied backup as an alternate manifest.
Changing the meaning of an existing field requires a schema/version migration and regression fixtures.

## SDK and mod lifecycle

Mods declare `supportedSdkVersionRange` and must use only public abstractions. Public SDK additions are additive within a
minor line. A planned removal must be documented and deprecated for at least one minor release unless retaining it
would preserve a critical security flaw. Lifecycle callbacks may be repeated across failures or scene changes; mods
must unregister handlers/services, release Unity objects, and tolerate partial startup.

First-party mods follow the same package, permission, dependency, error-isolation, and lifecycle rules as community
mods. They must not rely on privileged internal access.

## Dependencies and load order

Required dependencies participate in version solving and block installation/load when unsatisfied. Optional
dependencies affect ordering or integrations only when present. Conflicts block a plan. `loadBefore`/`loadAfter` are
ordering constraints, not implicit dependencies; cycles and ambiguous selections are errors. Exact lock entries record
the selected version and integrity hash, while an explicit resolve operation may update them from declared ranges.

## Game and platform compatibility

The initial release supports Robotopia build **2227** only. A numeric Robotopia build `N` is represented in manifest
ranges as SemVer `0.0.N`, so the canonical version for this release is `0.0.2227`. First-party packages declare that
exact game version and the compatible loader line. If the launcher or loader cannot determine the installed build, a
package with a constrained game range is blocked; temporary safe mode remains available because it loads no mods.

The checked-in game archive pin, compatibility baseline, live installed-game result, package manifests, and release
BOM must all name the same build. A newer public game build stops candidate publication for a compatibility audit; CI
never follows it automatically. Live verification against a legally installed Robotopia build is a release gate and
must never be silently waived.

Windows x64, macOS universal, and Linux x64/Proton release artifacts are distinct. Platform support is claimed only
after the native runner builds and validates the artifact. Unity-authored worlds and UI assets must record Unity
`6000.0.23f1`, target platform, hashes, and fallback behavior; a bundle built for one Unity target is not presumed
portable to another. Initial custom-world bundles target `StandaloneWindows64` and are supported only on Windows or
through the documented Proton/Wine path, not by the native macOS player.

## Registry, updates, and trust

Registry and release catalogs are untrusted data. Published mod-package assets are immutable for a version, use HTTPS,
declare SHA-256 integrity, and are validated before atomic installation. Redirects, size limits, timeouts, archive
paths, links, case-fold collisions, and rollback are enforced by the mod-package client. The initial launcher consumes
no self-update instructions: the published catalog is marked `manualOnly`, and launcher upgrades remain manual until
owner-signed metadata verification and bounded extraction are available.

The official registry initially contains only release-engineering-generated first-party entries. Community submission
and automatic deployment are closed until namespace ownership, moderation, malware response, revocation, appeal, and
installed-user response policies are approved. Authors can still publish through a self-hosted format-version-1
registry or a local package source.

A registry listing is not a security endorsement. C# mods execute in the game process with the user's authority. The
publisher is responsible for license/provenance, changelog, support, and vulnerability response; users must choose
which publishers they trust.
