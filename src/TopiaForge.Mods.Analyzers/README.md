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
- uses an SDK API newer than its manifest's minimum loader version (TF1010).
- declares an invalid map world, an unpacked file or a missing required Worlds dependency (TF1011).

TF1010 reads `RequiresTopiaForge` from shipped SDK metadata. Its code fix raises both the loader and
SDK floors in simple `>=A <B` manifest ranges without widening their ceilings. Other ranges need an
intentional manifest edit. See [version gates](https://docs.topiaforge.dev/diagnostics/TF1010).

It also warns when a mod reads an input press or release in a FixedUpdate handler, which misses or
repeats presses and, in multiplayer, never counts as the player's own action.

Manifest checks parse the JSON structure: matching text in descriptions, dependency range values,
or `x-*` metadata never satisfies a dependency or capability. Each diagnostic links to a stable
remediation page. The package has no runtime component.
