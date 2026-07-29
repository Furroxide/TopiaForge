# TopiaForge mod analyzers

This package is included automatically by the TopiaForge V1 SDK. It reports actionable build errors when a safe mod:

- directly uses Unity, GameCode, or Harmony without the explicit unstable interop opt-in;
- targets a framework other than `netstandard2.1`;
- copies loader-owned TopiaForge runtime assemblies into its package;
- references a specialist module contract assembly without its exact root-level
  `dependencies` or `optionalDependencies` entry;
- references `TopiaForge.Mods.Interop.Unity` without the root-level `unsafe-native` capability; or
- references the loader-owned `TopiaForge.Mods.UnityUi` renderer instead of `Context.Ui`; or
- calls a retired pre-V1 authoring API.

Manifest checks parse the JSON structure: matching text in descriptions, dependency range values,
or `x-*` metadata never satisfies a dependency or capability. Each diagnostic links to a stable
remediation page. The package has no runtime component.
