part of '../launch_resolution.dart';

/// Maps a legacy request only when exactly one fully valid target preserves it.
/// The original selection remains durable until an explicit save or repair.
abstract final class LegacySelectionResolution {
  static LaunchSelection resolve(
    EffectiveProfile profile,
    LaunchSelection selection, [
    RuntimeObservation observation = RuntimeObservation.none,
  ]) {
    if (selection.kind != LaunchSelectionKind.unresolvedLegacy) {
      return selection;
    }
    final legacy = selection.legacy!;
    if (legacy.isEmpty) return const LaunchSelection.mainMenu();
    final value = legacy['worldSelection'];
    if (value is! Map<String, Object?>) return selection;
    if (value['launchIntoGamemode'] == false) {
      return const LaunchSelection.mainMenu();
    }
    if (value['launchIntoGamemode'] != true) return selection;
    final gamemode = value['gamemodeId'];
    final world = value['worldId'];
    final mode = value['loadMode'];
    if (gamemode is! String ||
        world is! String ||
        mode is! String ||
        !ModContributions.isValidDeclarationId(gamemode) ||
        !ModContributions.isValidDeclarationId(world)) {
      return selection;
    }
    final transition = switch (mode) {
      WorldSelection.additiveArena => ModTransitions.additiveArena,
      WorldSelection.sceneReplacement => ModTransitions.sceneReplacement,
      _ => null,
    };
    if (transition == null) return selection;
    final index = _OwnerIndex(profile);
    final matches = <LaunchSelection>[];
    for (final choice in _previewTargets(index, observation)) {
      final target = _findTarget(index, choice.id, []);
      if (target == null || !_idEquals(target.declaration.gamemode, gamemode)) {
        continue;
      }
      final request = LaunchRequest(
        targetId: target.declaration.id,
        worldOverride:
            _idEquals(target.declaration.world?.defaultWorldId ?? '', world)
            ? null
            : world,
        transitionOverride:
            target.declaration.transition ==
                ModLaunchTargetDeclaration.playerChoiceTransition
            ? transition
            : null,
      );
      final resolution = LaunchResolver.resolve(profile, request, observation);
      if (resolution.plan case final plan?) {
        if (_idEquals(plan.worldId, world) && plan.transition == transition) {
          matches.add(LaunchSelection.target(request));
        }
      }
    }
    return matches.length == 1 ? matches.single : selection;
  }
}
