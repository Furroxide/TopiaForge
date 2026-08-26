---
title: Retired Manifest V4
description: Migration notice for the pre-release TopiaForge manifest schema.
---

# Retired Manifest V4

Manifest V4 was retired before TopiaForge's first public release. TopiaForge accepts only
[Manifest V5](ManifestV5.md). This is an intentional pre-release break: there is no released V4
ecosystem to preserve, and keeping two schemas would make authoring, validation, and multiplayer
admission harder to understand.

V5 does not require multiplayer support. A V5 manifest with no `multiplayer` field is the canonical
standalone-only form and preserves ordinary mod behavior.

## Migrate

```sh
topiaforge migrate-manifest --project .
topiaforge restore
topiaforge check package .
```

The migration changes `schemaVersion` to `5` and retains the ordinary package contract. It does not
add multiplayer metadata, packages, or a provider dependency. To opt in separately, run:

```sh
topiaforge mod add multiplayer
topiaforge restore
topiaforge mod sync multiplayer
```

Loaders and tooling reject V4 with an actionable migration message; they never reinterpret it as
V5. The retired schema file remains as an always-rejecting editor signal for stale projects.
