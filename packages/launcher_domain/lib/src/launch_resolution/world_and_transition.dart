part of '../launch_resolution.dart';

void _validatePolicyReferences(
  _OwnerIndex index,
  _TargetMatch target,
  List<LaunchBlock> blocks,
) {
  final policy = target.declaration.world;
  if (policy == null) return;
  final references = {policy.defaultWorldId, ...policy.allow};
  for (final id in references) {
    final owner = index.owner(id, []);
    if (owner?.ambiguous == false && owner?.enabled != null) {
      _checkReference(
        index,
        target.package,
        owner!.enabled!,
        id,
        LaunchBlockCode.worldRefNotADependency,
        blocks,
      );
    }
    _lookupWorld(index, id, RuntimeObservation.none, blocks, staticOnly: true);
  }
}

_WorldMatch? _resolveWorld(
  _OwnerIndex index,
  EffectiveProfile profile,
  _TargetMatch target,
  LaunchRequest request,
  RuntimeObservation observation,
  RuntimeBindingSnapshot? bindings,
  List<LaunchBlock> blocks,
) {
  final policy = target.declaration.world;
  if (policy == null) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.worldNotDeclared, target.declaration.id),
    );
    return null;
  }
  final requested = request.worldOverride ?? policy.defaultWorldId;
  if ((request.worldOverride != null && policy.allowPlayerOverride != true) ||
      (policy.policy != ModWorldPolicy.openPolicy &&
          !_idEquals(requested, policy.defaultWorldId) &&
          !(policy.policy == ModWorldPolicy.listPolicy &&
              policy.allow.any((id) => _idEquals(id, requested))))) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.worldNotAdmittedByPolicy, requested),
    );
  }
  final world = _lookupWorld(index, requested, observation, blocks);
  if (world == null) return null;
  _checkConsentReferences(index, world, blocks);
  // References in the manifest were checked above. An open player choice is
  // consent-governed and does not manufacture a dependency on its owner.
  final fresh = bindings != null && bindings.matches(profile);
  final unbound = bindings == null
      ? observation.unboundWorldIds.any(
          (id) =>
              _idEquals(id, world.declaration.id) || _idEquals(id, world.id),
        )
      : fresh &&
            !bindings.boundWorldIds.any(
              (id) => _idEquals(id, world.declaration.id),
            );
  if (unbound) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.worldUnbound,
        world.id,
        world.package.version,
      ),
    );
  }
  if (fresh) {
    _bindingFailures(bindings, 'world', world.declaration.id, blocks);
  }
  if (!_supportsThisInstall(profile.install, index.manifest(world.package))) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.worldPlatformUnsupported,
        world.package.id,
        world.package.version,
      ),
    );
  }
  for (final record in observation.availability) {
    if (record.kind == 'world' &&
        (_idEquals(record.id, world.id) ||
            _idEquals(record.id, world.declaration.id))) {
      blocks.addAll(
        record.blocks.where(
          (block) => block.code != LaunchBlockCode.worldUnbound,
        ),
      );
    }
  }
  return world;
}

_WorldMatch? _lookupWorld(
  _OwnerIndex index,
  String id,
  RuntimeObservation observation,
  List<LaunchBlock> blocks, {
  bool staticOnly = false,
}) {
  final owner = index.owner(id, blocks);
  if (owner?.ambiguous == true) return null;
  final package = owner?.enabled;
  if (package == null) {
    final off = owner?.disabled;
    blocks.add(
      off == null
          ? LaunchBlock(LaunchBlockCode.worldNotDeclared, id)
          : LaunchBlock(
              LaunchBlockCode.worldPackageDisabled,
              off.id,
              off.version,
            ),
    );
    return null;
  }
  final declared =
      index.manifest(package).contributions?.worlds ??
      const <ModWorldDeclaration>[];
  final exact = declared.where((world) => _idEquals(world.id, id)).toList();
  if (exact.length > 1) {
    blocks.add(LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, id));
    return null;
  }
  for (final world in exact) {
    if (!_idEquals(world.id, id)) continue;
    if (_isDiscovered(world)) {
      blocks.add(LaunchBlock(LaunchBlockCode.worldNotStaticallyDeclared, id));
    }
    return _WorldMatch(world, package);
  }
  final families =
      declared
          .where(
            (world) =>
                _isDiscovered(world) &&
                id.toLowerCase().startsWith('${world.id.toLowerCase()}.'),
          )
          .toList()
        ..sort((a, b) => b.id.length.compareTo(a.id.length));
  if (families.isEmpty) {
    blocks.add(LaunchBlock(LaunchBlockCode.worldNotDeclared, id));
    return null;
  }
  final family = families.first;
  if (families.where((item) => item.id.length == family.id.length).length > 1) {
    blocks.add(LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, id));
    return null;
  }
  final observed = observation.discoveredWorlds
      .where(
        (item) => _idEquals(item.id, id) && _idEquals(item.familyId, family.id),
      )
      .firstOrNull;
  if (staticOnly) {
    blocks.add(LaunchBlock(LaunchBlockCode.worldNotStaticallyDeclared, id));
  } else if (observed == null) {
    blocks.add(LaunchBlock(LaunchBlockCode.worldNotDeclared, id));
    return null;
  }
  return _WorldMatch(family, package, observed?.id ?? id);
}

void _checkConsent(
  _OwnerIndex index,
  _TargetMatch target,
  _GamemodeMatch mode,
  _WorldMatch world,
  List<LaunchBlock> blocks,
) {
  if (target.declaration.world?.policy != ModWorldPolicy.openPolicy) return;
  final explicit = world.declaration.openTo.any(
    (id) => _idEquals(id, mode.declaration.id),
  );
  if (explicit) {
    _checkReference(
      index,
      world.package,
      mode.package,
      mode.declaration.id,
      LaunchBlockCode.worldConsentRefNotADependency,
      blocks,
    );
  } else if (world.declaration.openToAnyCompatible != true) {
    blocks.add(LaunchBlock(LaunchBlockCode.worldConsentMissing, world.id));
  }
}

String _resolveTransition(
  _TargetMatch target,
  _GamemodeMatch gamemode,
  _WorldMatch world,
  LaunchRequest request,
  List<LaunchBlock> blocks,
) {
  final requirements = gamemode.declaration.worldRequirements;
  final required = requirements?.transitions ?? const <String>[];
  final offered = world.declaration.transitions
      .where((item) => required.isEmpty || required.contains(item))
      .toList();
  if (requirements?.spawn == ModSpawnPolicy.authoredMarkerKind &&
      world.declaration.spawn?.kind != ModSpawnPolicy.authoredMarkerKind) {
    blocks.add(
      LaunchBlock(LaunchBlockCode.spawnRequirementUnsatisfied, world.id),
    );
  }
  if (offered.isEmpty) {
    blocks.add(LaunchBlock(LaunchBlockCode.transitionUnsatisfiable, world.id));
  }
  final declared = target.declaration.transition;
  final choice = declared == ModLaunchTargetDeclaration.playerChoiceTransition;
  if (request.transitionOverride != null) {
    if (!choice || !offered.contains(request.transitionOverride)) {
      blocks.add(
        LaunchBlock(
          LaunchBlockCode.transitionNotOffered,
          request.transitionOverride!,
        ),
      );
      return '';
    }
    return request.transitionOverride!;
  }
  if (declared.isEmpty ||
      choice ||
      declared == ModLaunchTargetDeclaration.autoTransition) {
    return ModTransitions.byPrecedence.where(offered.contains).firstOrNull ??
        '';
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
  return install.gameVersion.isEmpty ||
      (SemanticVersion.tryParse(install.gameVersion) != null &&
          manifest.gameVersionRange.allows(install.gameVersion));
}

bool _unavailable(RuntimeObservation observation, String id) {
  final family = observation.discoveredWorlds
      .where((item) => _idEquals(item.id, id))
      .firstOrNull
      ?.familyId;
  return observation.unavailableWorldIds.any(
    (item) =>
        _idEquals(item, id) || (family != null && _idEquals(item, family)),
  );
}

bool _allCandidatesUnavailable(
  _OwnerIndex index,
  EffectiveProfile profile,
  _TargetMatch target,
  _GamemodeMatch gamemode,
  LaunchRequest request,
  RuntimeObservation observation,
) {
  final policy = target.declaration.world!;
  if (request.worldOverride != null ||
      policy.allowPlayerOverride != true ||
      policy.policy == ModWorldPolicy.fixedPolicy) {
    return _unavailable(
      observation,
      request.worldOverride ?? policy.defaultWorldId,
    );
  }
  final ids = <String>{policy.defaultWorldId};
  if (policy.policy == ModWorldPolicy.listPolicy) {
    ids.addAll(policy.allow);
  } else {
    for (final package in profile.packages) {
      for (final world
          in index.manifest(package).contributions?.worlds ??
              const <ModWorldDeclaration>[]) {
        if (!_isDiscovered(world)) ids.add(world.id);
      }
    }
    ids.addAll(observation.discoveredWorlds.map((item) => item.id));
  }
  final admitted = <String>[];
  for (final id in ids) {
    final candidateBlocks = <LaunchBlock>[];
    final world = _lookupWorld(index, id, observation, candidateBlocks);
    if (world == null || candidateBlocks.isNotEmpty) return false;
    if (policy.policy == ModWorldPolicy.openPolicy) {
      _checkConsentReferences(index, world, candidateBlocks);
      _checkConsent(index, target, gamemode, world, candidateBlocks);
      _resolveTransition(target, gamemode, world, request, candidateBlocks);
      if (candidateBlocks.isNotEmpty ||
          !_supportsThisInstall(
            profile.install,
            index.manifest(world.package),
          )) {
        continue;
      }
    }
    admitted.add(world.id);
  }
  return admitted.isNotEmpty &&
      admitted.every((id) => _unavailable(observation, id));
}
