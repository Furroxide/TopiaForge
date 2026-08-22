---
title: Sandbox creator gamemode
description: Use Robotopia's managed, disposable Creator workbench through safe Worlds, RobotKit, and Creator Content contracts.
---

# Sandbox creator gamemode

Sandbox is TopiaForge's managed, disposable Robotopia creator gamemode. Press **F5** to open the same fullscreen
workbench. Sandbox receives F5 routing priority and does not require
the ordinary-world persistence-isolation gate.

## Dependencies

The manifest declares schema V5 dependencies on Worlds, RobotKit, and Creator Content. The consumer project references
only their Unity-free contracts and links the shared workbench presentation/controller source. Providers are resolved
through the owner-scoped extension service.

## Lifecycle

Creator Content owns the single F5 action and routes it to Sandbox only while the Sandbox gamemode is active. The
legacy default Q binding migrates to F5; genuinely customized bindings remain unchanged. F5 or window dismissal hides
the editor and releases player controls without ending its session. The warning HUD remains until the destructive
**End Session & Restore** action is confirmed.

World exit, scene replacement, mod unload, and partial-load failure follow the same reverse-order, idempotent cleanup
path. Cleanup stops the graph, restores borrowed edits, removes owned content, and releases UI/focus resources.

## Feature coverage

- Searchable RobotKit and Creator Content catalog plus live scene roster.
- Spawn, select, transform, safe duplicate, temporary native hide, despawn, undo, and cleanup.
- Robot brain mode, real temporary personality, objective, emote, and bounded conversation tests.
- Local event-project list/save/load/delete and a typed, acyclic visual graph runner.
- TopiaForgeUi fullscreen Paper presentation with split panes, graph canvas, status bar, destructive modal, warning
  HUD, keyboard focus, scale, high contrast, and reduced motion.

Remote dialogue remains optional and has a deterministic fallback. Any unavailable Robotopia
provider binding disables only the affected feature and reports the reason in the mod's attributed
diagnostics.

Sandbox is a live acceptance case, not a substitute for the smaller generated templates. See [Creator Content](CoreServices.md)
for the shared safety and event-project model. New modders should begin with [Your first mod](YourFirstMod.md), then
add [Specialist modules](Modules.md) as their feature needs grow.
