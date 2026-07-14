# Registry Intake (Closed for the Initial Release)

The built-in Robotopia registry contains only release-generated first-party entries for v1. Community submissions
are closed until namespace ownership, moderation, malware review, takedown/revocation, appeals, and installed-user
response policies have named owners and an approved implementation. A change under `registry/**` is validation input;
it is not automatically published and does not make a package official or endorsed.

The repository intentionally retains the format-version-1 entry, append-only history, package, schema, and dependency
validation tooling so those controls can be tested without opening intake. CI rejects community publication while the
submissions-closed policy is active.

## Supported community path: self-hosting

Authors can publish an independent static registry now:

1. Create a scaffold with explicit author and license choices, then build and validate the package to zero findings.
2. Host the immutable `.robotopiamod` bytes at a stable HTTPS URL.
3. Generate and validate a format-version-1 entry/index with the `robotopia registry` commands.
4. Publish the index and package bytes atomically, then test adding that source and installing from a clean launcher.
5. Publish each update as a strictly newer version; never delete, reorder, or replace historical version bytes.

The full workflow and trust rules are in
[`docs/PublishingYourMod.md`](../docs/PublishingYourMod.md) and
[`docs/RegistryFormat.md`](../docs/RegistryFormat.md). Do not open an official community-registry pull request until
the project explicitly announces that intake has reopened; it will fail with a submissions-closed diagnostic.
