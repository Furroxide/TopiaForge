part of '../launch_resolution.dart';

/// Projects installed choices through the resolver and its ownership index.
abstract final class LaunchPreviewBuilder {
  static LaunchPreview build({
    required EffectiveProfile profile,
    required String installIdentity,
    required LaunchSelection requestedSelection,
    bool safeMode = false,
    RuntimeObservation observation = RuntimeObservation.none,
    Iterable<LauncherIssue> issues = const [],
  }) {
    final index = _OwnerIndex(profile);
    final observed = observation.matches(profile)
        ? observation
        : RuntimeObservation.none;
    final effective = safeMode
        ? const LaunchSelection.mainMenu()
        : LegacySelectionResolution.resolve(
            profile,
            requestedSelection,
            observed,
          );
    final request = effective.request;
    final result = request == null
        ? null
        : LaunchResolver.resolve(profile, request, observed);
    final target = request == null
        ? null
        : _findTarget(index, request.targetId, []);
    final worldOverride =
        target?.declaration.world?.allowPlayerOverride == true;
    final transitionOverride =
        target?.declaration.transition ==
        ModLaunchTargetDeclaration.playerChoiceTransition;
    final worlds = target != null && worldOverride
        ? _previewWorlds(index, target, observed)
        : const <LaunchWorldChoice>[];
    final transitions = <String>[];
    if (request != null && transitionOverride) {
      for (final transition in ModTransitions.byPrecedence) {
        final candidate = LaunchResolver.resolve(
          profile,
          LaunchRequest(
            targetId: request.targetId,
            worldOverride: request.worldOverride,
            transitionOverride: transition,
          ),
          observed,
        );
        if (_previewAdmitted(candidate.blocks)) {
          transitions.add(transition);
        }
      }
    }
    return LaunchPreview(
      profileId: profile.profileId,
      profileRevision: profile.revision,
      installIdentity: installIdentity,
      packageDigest: packageSetDigest(profile.packages),
      requestedSelection: requestedSelection,
      effectiveSelection: effective,
      packages: profile.packages,
      issues: issues,
      resolution: result,
      targets: _previewTargets(index, observed),
      worlds: worlds,
      transitions: transitions,
      allowWorldOverride: worldOverride,
      allowTransitionOverride: transitionOverride,
    );
  }
}

List<LaunchTargetChoice> _previewTargets(
  _OwnerIndex index,
  RuntimeObservation observation,
) {
  final candidates = <_TargetMatch>[];
  for (final package in [
    ...index.profile.packages,
    ...index.profile.disabledPackages,
  ]) {
    for (final target
        in index.manifest(package).contributions?.launchTargets ??
            const <ModLaunchTargetDeclaration>[]) {
      if (_idEquals(index.owner(target.id, [])?.id ?? '', package.id)) {
        candidates.add(_TargetMatch(target, package));
      }
    }
  }
  candidates.sort((a, b) {
    for (final order in [
      a.declaration.id.compareTo(b.declaration.id),
      a.package.version.compareTo(b.package.version),
      a.declaration.title.compareTo(b.declaration.title),
    ]) {
      if (order != 0) return order;
    }
    return 0;
  });
  final seen = <String>{};
  final choices = <LaunchTargetChoice>[];
  for (final candidate in candidates) {
    final target = candidate.declaration;
    if (!seen.add(target.id.toLowerCase())) continue;
    final resolution = LaunchResolver.resolve(
      index.profile,
      LaunchRequest(targetId: target.id),
      observation,
    );
    choices.add(
      LaunchTargetChoice(
        id: target.id,
        title: target.title,
        description: target.description,
        owner: candidate.package,
        order: target.sortKey ?? 0,
        blocks: resolution.blocks,
      ),
    );
  }
  choices.sort((a, b) {
    final order = a.order.compareTo(b.order);
    return order == 0 ? a.id.compareTo(b.id) : order;
  });
  return choices;
}

List<LaunchWorldChoice> _previewWorlds(
  _OwnerIndex index,
  _TargetMatch target,
  RuntimeObservation observation,
) {
  final ids = <String>{};
  for (final package in index.profile.packages) {
    for (final world
        in index.manifest(package).contributions?.worlds ??
            const <ModWorldDeclaration>[]) {
      if (!_isDiscovered(world)) ids.add(world.id);
    }
  }
  ids.addAll(observation.discoveredWorlds.map((world) => world.id));
  final choices = <LaunchWorldChoice>[];
  for (final id in ids.toList()..sort()) {
    final world = _lookupWorld(index, id, observation, []);
    if (world == null) continue;
    final resolution = LaunchResolver.resolve(
      index.profile,
      LaunchRequest(targetId: target.declaration.id, worldOverride: id),
      observation,
    );
    if (!_previewAdmitted(resolution.blocks)) continue;
    final discovered = observation.discoveredWorlds
        .where((item) => _idEquals(item.id, world.id))
        .firstOrNull;
    choices.add(
      LaunchWorldChoice(
        id: world.id,
        name: discovered?.name ?? world.declaration.name,
        familyId: discovered?.familyId,
        owner: world.package,
        blocks: resolution.blocks,
      ),
    );
  }
  return choices;
}

// Binding/readiness failures may leave an admitted choice visible for repair.
// Structural, dependency, consent and compatibility failures do not offer it.
bool _previewAdmitted(List<LaunchBlock> blocks) => blocks.every(
  (block) => const {
    LaunchBlockCode.gamemodeUnbound,
    LaunchBlockCode.worldUnbound,
    LaunchBlockCode.worldUnavailable,
    LaunchBlockCode.noAvailableTarget,
  }.contains(block.code),
);
