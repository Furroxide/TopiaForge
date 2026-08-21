---
title: Your first mod
description: Create, test, install, and run a safe mod for Robotopia in four commands.
---

# Your first mod

This walkthrough takes you from an empty directory to a mod running inside Robotopia. It starts with
a TopiaForge release and the pinned .NET SDK, and does not require a source checkout, Unity editor,
or direct knowledge of Robotopia's implementation APIs.

## 1. Check the toolchain

```sh
topiaforge doctor --strict
```

The command reports the selected TopiaForge release, exact .NET SDK, Robotopia installation, and
loader state. Follow its remediation if a required row is not ready.

## 2. Create a project

```sh
topiaforge new mod example.first-mod --name "First Mod" --author "You" --license AGPL-3.0-or-later --version 1.0.0
cd example.first-mod
```

The generated project contains:

```text
example.first-mod/
├── global.json
├── packages.lock.json
├── topiaforge.sdk.lock.json
├── topiaforge.mod.json
├── topiaforge.project.json
├── ExampleFirstMod.csproj
├── ExampleFirstModConfig.cs
├── ExampleFirstModMod.cs
└── tests/ExampleFirstMod.Tests/
```

`global.json` pins the SDK. The lock files make restores repeatable. The generated build props are
local output and can be recreated anywhere by `topiaforge restore`.

## 3. Read the entry point

The minimal template loads validated configuration, registers a namespaced command, and logs a
friendly message. The following code is inserted from the compiled template rather than copied into
this guide:

<!-- topiaforge-snippet path="templates/mod/minimal/{{TYPE_NAME}}Mod.cs" -->

Notice what is absent: owner ids, filesystem paths, global cleanup calls, Robotopia or Unity object types, and
manual event teardown. `Context` is attached before `OnLoad()`, and the command registration belongs
to the mod lifetime automatically.

## 4. Run the development loop

```sh
topiaforge dev
```

That one command restores exact SDK packages, builds, runs the NUnit project, packs, validates,
installs, launches Robotopia, and tails attributed logs. It stops before install if any earlier
stage fails.

The launch-blocking local Windows and same-host WSL2/Proton acceptance gates repeat this journey with a
clean candidate developer payload built from the frozen SHA: its CLI runs `new mod`, then
`dev --launch`, and the release handoff requires that unique mod's attributed load marker in the
same fresh `last-run.json`. The project lives outside that payload and requires no Unity
installation. The separate final clean-machine release gate repeats the journey with the actual
extracted platform archive and no source checkout.

In Robotopia, open the TopiaForge manager with F10. Select **First Mod** to see its log. Run the
`example.first-mod:greet` command from the manager command console to exercise the scaffolded
behavior.

## 5. Make a change

Edit the default greeting in `ExampleFirstModConfig.cs`, add a test assertion, and run
`topiaforge dev` again. A running Robotopia instance must restart before it can load changed assembly bytes.

## Where next

- Use [Core services](CoreServices.md) to add input, player, physics, entities, assets, audio, or UI.
- Add creator content, robots, worlds, time control, prompt overrides, UGC, or multiplayer through
  [Specialist modules](Modules.md).
- Use [Creator Tools](CreatorTools.md) when your mod should contribute safe catalog content or work
  with reversible creator sessions.
- Read [Test a mod](TestingMods.md) before adding behavior with several resource handles.
- Use [Manifest V5](ManifestV5.md) for dependencies, constraints, capabilities, optional multiplayer metadata, and exported contracts.
- See [Diagnostics](Diagnostics.md) when a stable `TF` code appears.
