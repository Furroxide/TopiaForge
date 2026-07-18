---
title: Advanced interop
description: Understand the explicitly unstable escape hatch for exceptional low-level mods.
---

# Advanced interop

The safe SDK is the V1 compatibility contract. Use it for ordinary utility, gameplay, world,
robot, story, asset, audio, and UI mods.

`TopiaForge.Mods.Interop.Unity` is an explicitly unstable escape hatch for advanced adapters and
low-level compatibility or performance patches. It is absent from normal templates, requires the
`unsafe-native` capability, needs locally restored Robotopia reference assemblies, and is excluded from
V1 source and binary compatibility guarantees.

## Before opting in

If the safe SDK cannot express a common modding goal, open an SDK capability request. Adding a
typed, testable facade helps every mod and lets providers absorb Robotopia updates in one place.

Interop is appropriate only when all of these are true:

- the feature is inherently a low-level adapter or patch;
- no core service or specialist module owns the behavior;
- graceful failure on unsupported Robotopia builds is implemented;
- every acquired hook and resource has idempotent teardown; and
- the package clearly discloses its unstable status to users and consumers.

## Add the package

```sh
topiaforge mod add interop-unity
topiaforge restore
```

The command adds the exact interop contract package and the `unsafe-native` capability together.
If Robotopia reference assemblies are unavailable, build error `TF1101` explains how to restore them.

Do not publish native types through `apiAssemblies`. Consumer contracts must remain in a separate,
engine-independent assembly and use SDK values or opaque handles. A provider may translate behind
that boundary, but safe consumers must not inherit its compatibility risk.

The TopiaForge team keeps Performance, PerfFixes, and NoFeedbackUrl as narrowly allowlisted advanced
packages. They are examples of the exception, not the recommended authoring path.
