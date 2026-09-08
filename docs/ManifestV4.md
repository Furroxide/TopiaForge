---
title: Retired Manifest V4
description: Migration notice for the retired pre-release TopiaForge manifest schema.
---

# Retired Manifest V4

Manifest V4 is retired. The loader, launcher and CLI accept
[Manifest V6](ManifestV6.md). The V4 schema is an always-rejecting stub,
so an editor cannot mistake an old manifest for a supported package.

```sh
topiaforge migrate-manifest --project .
topiaforge restore
topiaforge check package .
```

V3, V4 and V5 use the same preservation and refusal rules after mechanical
normalization. Untouched JSON values and property presence survive successful
migration; formatting may change. Ordinary packages can migrate automatically
when all required information is present. Multiplayer remains optional.

Metadata-only `worldGamemodes` cannot supply a factory, world, target or spawn
policy. Normal migration reports the file, original entry index and ID and
leaves the file unchanged. An explicit `--stub` can write an intentionally
invalid V6 document with the original declarations and `x-migration-todo` notes;
it exits unsuccessfully until the author completes the required fields.
Malformed legacy collections or entries are refused even in stub mode.

See [Migration from V3, V4 or V5](ManifestV6.md#migration-from-v3-v4-or-v5)
for the complete command and failure behavior. Historical source remains in Git;
no compatibility promise requires loading these unreleased contracts.
