part of '../launch_resolution.dart';

abstract final class LaunchResolver {
  static LaunchResolution resolve(
    EffectiveProfile profile,
    LaunchRequest request, [
    RuntimeObservation observation = RuntimeObservation.none,
    RuntimeBindingSnapshot? bindings,
  ]) {
    final blocks = <LaunchBlock>[];
    final index = _OwnerIndex(profile);
    blocks.addAll(index.ambiguities);
    final target = _findTarget(index, request.targetId, blocks);
    if (target == null) return LaunchResolution.blocked(blocks);
    if (bindings != null && !bindings.matches(profile)) {
      return LaunchResolution.blocked([
        LaunchBlock(
          LaunchBlockCode.planPackageSetMismatch,
          target.declaration.id,
        ),
      ]);
    }
    if (!_supportsThisInstall(
      profile.install,
      index.manifest(target.package),
    )) {
      blocks.add(
        LaunchBlock(
          LaunchBlockCode.targetPlatformUnsupported,
          target.package.id,
          target.package.version,
        ),
      );
    }
    final validObservation = observation.matches(profile)
        ? observation
        : RuntimeObservation.none;
    final gamemode = _resolveGamemode(
      index,
      target,
      validObservation,
      bindings,
      blocks,
    );
    _validatePolicyReferences(index, target, blocks);
    final world = _resolveWorld(
      index,
      profile,
      target,
      request,
      validObservation,
      bindings,
      blocks,
    );
    String transition = '';
    if (gamemode != null && world != null) {
      transition = _resolveTransition(target, gamemode, world, request, blocks);
      _checkConsent(index, target, gamemode, world, blocks);
      if (blocks.every(
            (block) => block.code == LaunchBlockCode.worldUnavailable,
          ) &&
          _allCandidatesUnavailable(
            index,
            profile,
            target,
            gamemode,
            request,
            validObservation,
          )) {
        blocks.add(
          LaunchBlock(LaunchBlockCode.noAvailableTarget, target.declaration.id),
        );
      }
    }
    if (blocks.isNotEmpty) return LaunchResolution.blocked(blocks);
    return LaunchResolution.success(
      LaunchPlan._(
        targetId: target.declaration.id,
        gamemodeId: gamemode!.declaration.id,
        worldId: world!.instanceId ?? world.declaration.id,
        worldFamilyId: world.instanceId == null ? null : world.declaration.id,
        transition: transition,
        request: request,
        packages: profile.packages,
        target: target.declaration,
        gamemode: gamemode.declaration,
        world: world.declaration,
      ),
    );
  }

  static List<LaunchBlock> revalidate(
    LaunchPlanDescriptor plan,
    Iterable<PackageIdentity> loaded,
  ) {
    final copy = loaded.toList();
    return _samePackages(plan.packages, copy) &&
            packageSetDigest(copy) == plan.digest
        ? const []
        : [LaunchBlock(LaunchBlockCode.planPackageSetMismatch, plan.targetId)];
  }

  static LaunchResolution resolveAgain(
    LaunchPlanDescriptor plan,
    EffectiveProfile loaded, [
    RuntimeObservation observation = RuntimeObservation.none,
    RuntimeBindingSnapshot? bindings,
  ]) {
    final mismatch = revalidate(plan, loaded.packages);
    if (mismatch.isNotEmpty) return LaunchResolution.blocked(mismatch);
    final resolved = resolve(
      loaded,
      plan.request,
      observation,
      bindings ?? RuntimeBindingSnapshot._missing(loaded),
    );
    final next = resolved.plan;
    if (next == null) return resolved;
    if (next.targetId != plan.targetId ||
        next.gamemodeId != plan.gamemodeId ||
        next.worldId != plan.worldId ||
        next.worldFamilyId != plan.worldFamilyId ||
        next.transition != plan.transition) {
      return LaunchResolution.blocked([
        LaunchBlock(LaunchBlockCode.planResolutionMismatch, plan.targetId),
      ]);
    }
    return resolved;
  }
}

class _TargetMatch {
  const _TargetMatch(this.declaration, this.package);
  final ModLaunchTargetDeclaration declaration;
  final ResolvedPackage package;
}

class _GamemodeMatch {
  const _GamemodeMatch(this.declaration, this.package);
  final ModGamemodeDeclaration declaration;
  final ResolvedPackage package;
}

class _WorldMatch {
  const _WorldMatch(this.declaration, this.package, [this.instanceId]);
  final ModWorldDeclaration declaration;
  final ResolvedPackage package;
  final String? instanceId;
  String get id => instanceId ?? declaration.id;
}

_TargetMatch? _findTarget(
  _OwnerIndex index,
  String id,
  List<LaunchBlock> blocks,
) {
  final owner = index.owner(id, blocks);
  if (owner?.ambiguous == true) return null;
  final package = owner?.enabled;
  if (package == null) {
    final off = owner?.disabled;
    blocks.add(
      off == null
          ? LaunchBlock(LaunchBlockCode.targetNotDeclared, id)
          : LaunchBlock(
              LaunchBlockCode.targetPackageDisabled,
              off.id,
              off.version,
            ),
    );
    return null;
  }
  final targets =
      (index.manifest(package).contributions?.launchTargets ??
              const <ModLaunchTargetDeclaration>[])
          .where((item) => _idEquals(item.id, id))
          .toList();
  if (targets.length > 1) {
    blocks.add(LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, id));
    return null;
  }
  if (targets.isNotEmpty) return _TargetMatch(targets.single, package);
  blocks.add(LaunchBlock(LaunchBlockCode.targetNotDeclared, id));
  return null;
}

_GamemodeMatch? _resolveGamemode(
  _OwnerIndex index,
  _TargetMatch target,
  RuntimeObservation observation,
  RuntimeBindingSnapshot? bindings,
  List<LaunchBlock> blocks,
) {
  final id = target.declaration.gamemode;
  final owner = index.owner(id, blocks);
  if (owner?.ambiguous == true) return null;
  final package = owner?.enabled;
  if (package == null) {
    final off = owner?.disabled;
    blocks.add(
      off == null
          ? LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, id)
          : LaunchBlock(
              LaunchBlockCode.gamemodePackageDisabled,
              off.id,
              off.version,
            ),
    );
    return null;
  }
  _checkReference(
    index,
    target.package,
    package,
    id,
    LaunchBlockCode.gamemodeRefNotADependency,
    blocks,
  );
  final modes =
      index.manifest(package).contributions?.gamemodes ??
      const <ModGamemodeDeclaration>[];
  final matching = modes.where((mode) => _idEquals(mode.id, id)).toList();
  if (matching.length > 1) {
    blocks.add(LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, id));
    return null;
  }
  for (final mode in matching) {
    if (!_supportsThisInstall(index.profile.install, index.manifest(package))) {
      blocks.add(
        LaunchBlock(
          LaunchBlockCode.gamemodePlatformUnsupported,
          package.id,
          package.version,
        ),
      );
    }
    final fresh = bindings != null && bindings.matches(index.profile);
    final unavailable = bindings == null
        ? observation.unboundGamemodeIds.any((item) => _idEquals(item, mode.id))
        : fresh &&
              !bindings.boundGamemodeIds.any(
                (item) => _idEquals(item, mode.id),
              );
    if (unavailable) {
      blocks.add(
        LaunchBlock(LaunchBlockCode.gamemodeUnbound, mode.id, package.version),
      );
    }
    if (fresh) _bindingFailures(bindings, 'gamemode', mode.id, blocks);
    for (final record in observation.availability) {
      if (record.kind == 'gamemode' && _idEquals(record.id, mode.id)) {
        blocks.addAll(
          record.blocks.where(
            (block) =>
                !(fresh && block.code == LaunchBlockCode.gamemodeUnbound),
          ),
        );
      }
    }
    return _GamemodeMatch(mode, package);
  }
  blocks.add(LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, id));
  return null;
}

void _bindingFailures(
  RuntimeBindingSnapshot? bindings,
  String kind,
  String id,
  List<LaunchBlock> blocks,
) {
  if (bindings == null) return;
  for (final record in bindings.availability) {
    if (record.kind == kind && _idEquals(record.id, id)) {
      blocks.addAll(record.blocks);
    }
  }
}

bool _idEquals(String left, String right) =>
    left.toLowerCase() == right.toLowerCase();
bool _isDiscovered(ModWorldDeclaration world) =>
    world.content?.kind == ModWorldContent.discoveredKind;
