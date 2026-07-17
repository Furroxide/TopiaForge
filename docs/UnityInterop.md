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

## Owner-scoped Harmony patches

Do not create a long-lived `Harmony` owner and remember to unpatch it manually. Acquire an owner-scoped
lease from the interop context instead:

```csharp
using System.Reflection;
using TopiaForge.Mods.Interop.Unity;

var patches = Context.CreateHarmonyLease("camera-fixes");
patches.Patch(targetMethod, prefix: prefixMethod);
```

The runtime derives a process-unique Harmony id from the manifest owner and purpose, tracks the lease in
`Context.Lifetime`, and removes every patch carrying that id after normal unload and partial-load failure.
Disposal is idempotent and may be requested early. Creating a lease, applying a patch, and the first disposal
must run on Robotopia's game thread; queue worker-originated work with `Context.Scheduler.NextFrame`. After
teardown completes, repeated disposal is a thread-safe no-op. Patch application and teardown are serialized so
a patch cannot land after the teardown sweep. The runtime keeps the Harmony owner id private; all patches must
go through the lease to retain that guarantee. Consumers do not need a compile-time 0Harmony reference—the
game runtime supplies Harmony.
