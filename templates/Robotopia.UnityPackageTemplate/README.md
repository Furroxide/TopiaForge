# Robotopia / QuantumWorks Unity package template

A starter QuantumWorks **VPM** Unity package (the equivalent of VRChat's `template-package`).

Scaffold one with `robotopia unity new-package <com.you.your-package> [--name "Display Name"] [--dir path]`, then:

1. Put always-included code in `Runtime/`, editor-only tooling in `Editor/`, and optional sample assets in `Samples~/`.
2. Edit `package.json` (`name`, `version`, `displayName`, `unity`, `vpmDependencies`, `samples`).
3. Build your package and integrity-pinned listing from the package directory:

   ```text
   robotopia unity pack-packages --package . --output dist/vpm --repo-id com.you.repo --repo-name "Your Repository" --author "Your Name"
   ```

   Repeat `--package <dir>` to publish several packages in one listing. The command validates each root,
   writes deterministic package zips plus `index.json`, and records every zip SHA-256 in the listing.
4. Upload the complete `dist/vpm/` directory to a stable HTTPS location, then subscribe with
   `robotopia unity add-repo <https-url>/index.json` and test a clean resolve before announcing it.

See `docs/UnityVpm.md` in the QuantumWorks repo for the package/manifest/listing formats and the resolver.
