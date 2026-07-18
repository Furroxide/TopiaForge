# TopiaForge V1 C# API

This reference is generated from every XML-documented V1 Robotopia authoring package: the Unity-free core and
specialist contracts, the testing kit, and the explicitly unstable Unity interop escape hatch.
Start with the [modding guide](https://docs.topiaforge.dev/guides/core-services/) for task-oriented examples.

The normal Robotopia authoring surface begins with `TopiaForgeMod` and `IModContext`. Specialist module
contracts are installed with `topiaforge mod add <module>`. Types under
`TopiaForge.Mods.Interop.Unity` are capability-gated native APIs and are not covered by the V1
compatibility promise; ordinary mods should stay on the safe contracts.
