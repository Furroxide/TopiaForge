part of '../launch_resolution.dart';

// Which world a target admits, and how the session enters it. Split from the
// resolver so both stay under the 500-line non-generated Dart cap, and because
// these are the two decisions with real policy in them: everything else is
// lookup.

_WorldMatch? _resolveWorld(
  List<ResolvedPackage> packages,
  EffectiveProfile profile,
  _TargetMatch target,
  LaunchRequest request,
  RuntimeObservation observation,
  List<LaunchBlock> blocks,
) {
  final policy = target.declaration.world;
  if (policy == null) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.worldNotDeclared, target.declaration.id),
    );
    return null;
  }

  final requested = request.worldOverride.isNotEmpty
      ? request.worldOverride
      : policy.defaultWorldId;
  if (request.worldOverride.isNotEmpty && !_admitsChoice(policy)) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.worldNotAdmittedByPolicy, requested),
    );
    return null;
  }

  final owner = _ownerOf(packages, requested, blocks);
  if (owner == null) {
    final off = _disabledDeclaring(
      profile.disabledPackages,
      (candidate) =>
          requested.toLowerCase().startsWith('${candidate.id.toLowerCase()}.'),
    );
    blocks.add(
      off == null
          ? LaunchBlock(LaunchBlockCode.worldNotDeclared, requested)
          : LaunchBlock(
              LaunchBlockCode.worldPackageDisabled,
              off.id,
              off.version,
            ),
    );
    return null;
  }
  final worldOwnership = _referenceOwnership(target.package, owner);
  if (worldOwnership != _ReferenceOwnership.owned) {
    blocks.add(
      LaunchBlock(
        worldOwnership == _ReferenceOwnership.versionUnsatisfied
            ? LaunchBlockCode.targetPackageVersionUnsatisfied
            : LaunchBlockCode.worldRefNotADependency,
        requested,
        owner.version,
      ),
    );
    return null;
  }

  final declared =
      owner.manifest.contributions?.worlds ?? const <ModWorldDeclaration>[];
  var instanceId = '';
  ModWorldDeclaration? world;
  for (final candidate in declared) {
    if (_idEquals(candidate.id, requested)) {
      world = candidate;
      break;
    }
  }

  if (world == null) {
    // Not a declared world, but it may be an observed member of a declared
    // family. A family id is a prefix; only its members are launchable.
    for (final candidate in declared) {
      if (_isDiscovered(candidate) &&
          requested.toLowerCase().startsWith(
            '${candidate.id.toLowerCase()}.',
          )) {
        world = candidate;
        break;
      }
    }
    if (world == null) {
      blocks.add(LaunchBlock(LaunchBlockCode.worldNotDeclared, requested));
      return null;
    }
    if (!observation.discoveredWorldIds.any((id) => _idEquals(id, requested))) {
      blocks.add(
        LaunchBlock(LaunchBlockCode.worldNotStaticallyDeclared, requested),
      );
      return null;
    }
    instanceId = requested;
  } else if (_isDiscovered(world)) {
    // The family itself, not a member of it. There is nothing to load.
    blocks.add(
      LaunchBlock(LaunchBlockCode.worldNotStaticallyDeclared, requested),
    );
    return null;
  }

  if (!_admittedByPolicy(
    policy,
    world,
    target.declaration.gamemode,
    requested,
    blocks,
  )) {
    return null;
  }

  if (observation.unavailableWorldIds.any((id) => _idEquals(id, requested))) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.noAvailableTarget, target.declaration.id),
    );
    return null;
  }

  if (!_supportsThisInstall(profile.install, owner.manifest)) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.worldPlatformUnsupported,
        owner.id,
        owner.version,
      ),
    );
    return null;
  }

  return _WorldMatch(world, owner, instanceId);
}

bool _admittedByPolicy(
  ModWorldPolicy policy,
  ModWorldDeclaration world,
  String gamemodeId,
  String requested,
  List<LaunchBlock> blocks,
) {
  if (policy.policy == ModWorldPolicy.openPolicy) {
    // Consent is scoped to the open policy alone. Requiring it everywhere would
    // make a world's package depend on the gamemodes that use it, and the
    // first-party graph already runs the other way.
    if (_idEquals(requested, policy.defaultWorldId) ||
        world.openToAnyCompatible == true ||
        world.openTo.any((id) => _idEquals(id, gamemodeId))) {
      return true;
    }
    blocks.add(LaunchBlock(LaunchBlockCode.worldConsentMissing, requested));
    return false;
  }

  final admitted =
      _idEquals(requested, policy.defaultWorldId) ||
      (policy.policy == ModWorldPolicy.listPolicy &&
          policy.allow.any((id) => _idEquals(id, requested)));
  if (admitted) {
    return true;
  }
  blocks.add(LaunchBlock(LaunchBlockCode.worldNotAdmittedByPolicy, requested));
  return false;
}

/// Chooses the transition, deterministically.
///
/// Scene replacement outranks the additive arena, and the order is fixed rather
/// than discovered. A world that supports both ships today, so `auto` without a
/// stated precedence would mean a launch whose behaviour depends on declaration
/// order.
String _resolveTransition(
  _TargetMatch target,
  _GamemodeMatch gamemode,
  _WorldMatch world,
  LaunchRequest request,
  List<LaunchBlock> blocks,
) {
  final required = gamemode.declaration.worldRequirements?.transitions;
  final offered = world.declaration.transitions
      .where(
        (item) =>
            required == null || required.isEmpty || required.contains(item),
      )
      .toList();
  if (offered.isEmpty) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.transitionUnsatisfiable,
        world.declaration.id,
      ),
    );
    return '';
  }

  if (gamemode.declaration.worldRequirements?.spawn ==
          ModSpawnPolicy.authoredMarkerKind &&
      world.declaration.spawn?.kind != ModSpawnPolicy.authoredMarkerKind) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.spawnRequirementUnsatisfied,
        world.declaration.id,
      ),
    );
    return '';
  }

  final declared = target.declaration.transition;
  final offersChoice =
      declared == ModLaunchTargetDeclaration.playerChoiceTransition;
  if (request.transitionOverride.isNotEmpty) {
    if (!offersChoice || !offered.contains(request.transitionOverride)) {
      blocks.add(
        LaunchBlock(
          LaunchBlockCode.transitionNotOffered,
          request.transitionOverride,
        ),
      );
      return '';
    }
    return request.transitionOverride;
  }

  if (declared.isEmpty ||
      offersChoice ||
      declared == ModLaunchTargetDeclaration.autoTransition) {
    return ModTransitions.byPrecedence.firstWhere(offered.contains);
  }

  if (!offered.contains(declared)) {
    blocks.add(LaunchBlock(LaunchBlockCode.transitionNotOffered, declared));
    return '';
  }
  return declared;
}

bool _supportsThisInstall(InstallFacts install, ModManifest manifest) {
  bool unsupported(String actual, List<String> declared) =>
      actual.isNotEmpty &&
      declared.isNotEmpty &&
      !declared.any((item) => _idEquals(item, actual));

  if (unsupported(install.platform, manifest.platforms) ||
      unsupported(install.architecture, manifest.architectures) ||
      unsupported(install.contentTarget, manifest.contentTargets)) {
    return false;
  }
  if (install.gameVersion.isEmpty) {
    return true;
  }
  return SemanticVersion.tryParse(install.gameVersion) != null &&
      manifest.gameVersionRange.allows(install.gameVersion);
}

/// How a cross-package reference resolves, or fails to.
enum _ReferenceOwnership { owned, notADependency, versionUnsatisfied }

/// A reference is owned when the referencing package declares it, or requires
/// the package that does at a version the pin satisfies.
_ReferenceOwnership _referenceOwnership(
  ResolvedPackage referrer,
  ResolvedPackage owner,
) {
  if (_idEquals(referrer.id, owner.id)) {
    return _ReferenceOwnership.owned;
  }
  for (final dependency in referrer.manifest.dependencies) {
    if (!_idEquals(dependency.id, owner.id)) {
      continue;
    }
    // A dependency that is declared but pins the wrong version is a different
    // fix from one that was never declared, and saying so saves the author
    // guessing which.
    return SemanticVersion.tryParse(owner.version) != null &&
            dependency.versionRange.allows(owner.version)
        ? _ReferenceOwnership.owned
        : _ReferenceOwnership.versionUnsatisfied;
  }
  return _ReferenceOwnership.notADependency;
}

/// Finds the package that owns an id, by longest matching name.
///
/// Longest wins because a package id may contain dots, so a package named
/// `…topiaforge.worlds.mine` is a legal name sitting inside
/// `…topiaforge.worlds`'s namespace. Falling through to the shorter owner would
/// let one package answer for ids another package's name covers.
ResolvedPackage? _ownerOf(
  List<ResolvedPackage> packages,
  String declarationId,
  List<LaunchBlock> blocks,
) {
  final lowered = declarationId.toLowerCase();
  final candidates =
      packages
          .where(
            (package) => lowered.startsWith('${package.id.toLowerCase()}.'),
          )
          .toList()
        ..sort((left, right) => right.id.length.compareTo(left.id.length));
  if (candidates.isEmpty) {
    return null;
  }
  if (candidates.length > 1 &&
      candidates[0].id.length == candidates[1].id.length) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, declarationId),
    );
    return null;
  }
  return candidates.first;
}

bool _isDiscovered(ModWorldDeclaration world) =>
    world.content?.kind == ModWorldContent.discoveredKind;

bool _admitsChoice(ModWorldPolicy policy) =>
    policy.allowPlayerOverride == true ||
    policy.policy == ModWorldPolicy.openPolicy ||
    policy.policy == ModWorldPolicy.listPolicy;

bool _idEquals(String left, String right) =>
    left.toLowerCase() == right.toLowerCase();
