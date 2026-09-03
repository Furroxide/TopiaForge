part of '../launch_resolution.dart';

/// Turns a request to launch a target into one decided plan, or into every
/// reason there is none.
///
/// Pure data in, pure data out. No filesystem, no registry, no catalog file.
/// That constraint is the point: the launcher's preflight and the manager's own
/// view of a launch used to be two separate pieces of code reading two different
/// sources, and they disagreed. A launch could pass a check against a catalog
/// written by a previous run and then fail against the profile actually enabled.
///
/// Mirrored in `src/TopiaForge.ModManager.Core/LaunchResolver.cs`, and the
/// shared fixtures under `tests/fixtures/gamemode-v6/resolution` are what hold
/// the two together.
abstract final class LaunchResolver {
  /// Resolves one launch request against one effective profile.
  static LaunchResolution resolve(
    EffectiveProfile profile,
    LaunchRequest request, [
    RuntimeObservation observation = RuntimeObservation.none,
  ]) {
    final blocks = <LaunchBlock>[];
    final packages = [...profile.packages]
      ..sort((left, right) => left.id.compareTo(right.id));

    final target = _findTarget(
      packages,
      profile.disabledPackages,
      request.targetId,
      blocks,
    );
    if (target == null) {
      return LaunchResolution.blocked(blocks);
    }

    final gamemode = _resolveGamemode(
      packages,
      profile.disabledPackages,
      target,
      observation,
      blocks,
    );
    final world = _resolveWorld(
      packages,
      profile,
      target,
      request,
      observation,
      blocks,
    );
    if (gamemode == null || world == null) {
      return LaunchResolution.blocked(blocks);
    }

    final transition = _resolveTransition(
      target,
      gamemode,
      world,
      request,
      blocks,
    );
    if (blocks.isNotEmpty) {
      return LaunchResolution.blocked(blocks);
    }

    return LaunchResolution.success(
      LaunchPlan(
        launchTargetId: target.declaration.id,
        gamemodeId: gamemode.declaration.id,
        worldId: world.declaration.id,
        worldInstanceId: world.instanceId,
        transition: transition,
        resolvedPackages: List.unmodifiable(packages),
      ),
    );
  }

  /// Revalidates a plan against the set that actually loaded, before any scene
  /// work begins.
  ///
  /// This is what makes the preflight an invariant instead of a promise. A plan
  /// resolved against one package set and executed against another is exactly
  /// the disagreement the resolver exists to end, and it is invisible without
  /// this comparison.
  static List<LaunchBlock> revalidate(
    LaunchPlan plan,
    List<ResolvedPackage> loaded,
  ) => packageSetDigest(loaded) == plan.digest
      ? const []
      : [
          LaunchBlock(
            LaunchBlockCode.planPackageSetMismatch,
            plan.launchTargetId,
          ),
        ];
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
  const _WorldMatch(this.declaration, this.package, this.instanceId);
  final ModWorldDeclaration declaration;
  final ResolvedPackage package;
  final String instanceId;
}

_TargetMatch? _findTarget(
  List<ResolvedPackage> packages,
  List<ResolvedPackage> disabled,
  String targetId,
  List<LaunchBlock> blocks,
) {
  for (final package in packages) {
    for (final target
        in package.manifest.contributions?.launchTargets ??
            const <ModLaunchTargetDeclaration>[]) {
      if (_idEquals(target.id, targetId)) {
        return _TargetMatch(target, package);
      }
    }
  }

  // Installed but switched off is a different answer from no such thing, and
  // only one of them is a click away from working.
  final off = _disabledDeclaring(
    disabled,
    (candidate) =>
        (candidate.manifest.contributions?.launchTargets ??
                const <ModLaunchTargetDeclaration>[])
            .any((item) => _idEquals(item.id, targetId)),
  );
  blocks.add(
    off == null
        ? LaunchBlock(LaunchBlockCode.targetNotDeclared, targetId)
        : LaunchBlock(
            LaunchBlockCode.targetPackageDisabled,
            off.id,
            off.version,
          ),
  );
  return null;
}

ResolvedPackage? _disabledDeclaring(
  List<ResolvedPackage> disabled,
  bool Function(ResolvedPackage) declares,
) {
  for (final package in disabled) {
    if (declares(package)) {
      return package;
    }
  }
  return null;
}

_GamemodeMatch? _resolveGamemode(
  List<ResolvedPackage> packages,
  List<ResolvedPackage> disabled,
  _TargetMatch target,
  RuntimeObservation observation,
  List<LaunchBlock> blocks,
) {
  final reference = target.declaration.gamemode;
  final owner = _ownerOf(packages, reference, blocks);
  if (owner == null) {
    final off = _disabledDeclaring(
      disabled,
      (candidate) =>
          reference.toLowerCase().startsWith('${candidate.id.toLowerCase()}.'),
    );
    blocks.add(
      off == null
          ? LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, reference)
          : LaunchBlock(
              LaunchBlockCode.gamemodePackageDisabled,
              off.id,
              off.version,
            ),
    );
    return null;
  }

  // A reference out of the declaring package has to resolve through a dependency
  // that package requires. An optional one would make the launch work only where
  // the optional package happens to be installed, which is a failure the author
  // never sees.
  final gamemodeOwnership = _referenceOwnership(target.package, owner);
  if (gamemodeOwnership != _ReferenceOwnership.owned) {
    blocks.add(
      LaunchBlock(
        gamemodeOwnership == _ReferenceOwnership.versionUnsatisfied
            ? LaunchBlockCode.targetPackageVersionUnsatisfied
            : LaunchBlockCode.gamemodeRefNotADependency,
        reference,
        owner.version,
      ),
    );
    return null;
  }

  for (final gamemode
      in owner.manifest.contributions?.gamemodes ??
          const <ModGamemodeDeclaration>[]) {
    if (!_idEquals(gamemode.id, reference)) {
      continue;
    }
    if (observation.unboundGamemodeIds.any(
      (id) => _idEquals(id, gamemode.id),
    )) {
      blocks.add(
        LaunchBlock(
          LaunchBlockCode.gamemodeUnbound,
          gamemode.id,
          owner.version,
        ),
      );
      return null;
    }
    return _GamemodeMatch(gamemode, owner);
  }

  blocks.add(LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, reference));
  return null;
}
