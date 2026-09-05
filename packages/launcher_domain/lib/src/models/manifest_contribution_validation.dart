part of '../models.dart';

/// The V6 contribution rules that JSON Schema cannot express, and the ones it
/// can but the C# manager never sees.
///
/// Mirrors `ManifestContributionValidator` in
/// `src/TopiaForge.ModManager.Core`, message for message, because the codes the
/// shared fixtures compare are the leading path in each message. Nothing else
/// compares the two validators.
///
/// Every message opens with the path it is about, so a failure names the
/// declaration rather than the manifest.
void _validateManifestContributions(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  final contributions = manifest.contributions;
  if (contributions == null) {
    return;
  }

  void error(String message) => issues.add(
    LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: manifest.id,
      message: message,
    ),
  );

  // Declaring a launch surface means owning worlds at runtime, and
  // world-service is the capability that discloses it. The schema says this
  // with `contains`; the C# manager would not know it otherwise.
  if (!manifest.capabilities.contains('world-service')) {
    error(
      'contributions requires the world-service capability, because declaring '
      'worlds, gamemodes or launch targets means owning world content at '
      'runtime.',
    );
  }

  _contributionCount(
    contributions.worlds.length,
    'contributions.worlds',
    64,
    error,
  );
  _contributionCount(
    contributions.gamemodes.length,
    'contributions.gamemodes',
    16,
    error,
  );
  _contributionCount(
    contributions.launchTargets.length,
    'contributions.launchTargets',
    64,
    error,
  );

  final owned = _ownedDeclarationIds(manifest, error);
  final discovered = {
    for (final world in contributions.worlds)
      if (world.content?.kind == ModWorldContent.discoveredKind)
        world.id.toLowerCase(),
  };

  for (var index = 0; index < contributions.worlds.length; index++) {
    _validateWorldDeclaration(
      manifest,
      contributions.worlds[index],
      'contributions.worlds[$index]',
      error,
    );
  }
  for (var index = 0; index < contributions.gamemodes.length; index++) {
    _validateGamemodeDeclaration(
      manifest,
      contributions.gamemodes[index],
      'contributions.gamemodes[$index]',
      error,
    );
  }
  for (var index = 0; index < contributions.launchTargets.length; index++) {
    _validateLaunchTargetDeclaration(
      manifest,
      contributions.launchTargets[index],
      'contributions.launchTargets[$index]',
      owned,
      discovered,
      error,
    );
  }

  // R6, and a warning rather than an error on purpose: a gamemode no local
  // target names is how one package publishes a mode for another package's
  // target to point at, which is exactly what the world template does.
  //
  // The C# validator has no warning channel -- its contract is errors only --
  // so this is stated here alone. It cannot change a verdict, so the two
  // readers still agree on every accept and reject.
  final referenced = contributions.launchTargets
      .map((target) => target.gamemode.toLowerCase())
      .toSet();
  for (final gamemode in contributions.gamemodes) {
    if (!referenced.contains(gamemode.id.toLowerCase())) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.warning,
          subjectId: manifest.id,
          message:
              '${gamemode.id} is declared but no launch target in this manifest '
              'starts it. That is legal -- another package may target it -- but '
              'nothing here can.',
        ),
      );
    }
  }
}

/// R1 ownership and R2 uniqueness, together because both are about the id
/// alone. An id must be namespaced under the declaring package and strictly
/// longer than that prefix, so a package can never declare something in a
/// namespace it does not own -- and `id == name` is not a declaration, it is
/// the package.
Set<String> _ownedDeclarationIds(
  ModManifest manifest,
  void Function(String) error,
) {
  final prefix = '${manifest.id}.'.toLowerCase();
  final owned = <String>{};
  for (final declaration in _allDeclarations(manifest)) {
    final path = '${declaration.path}.id';
    if (!_isValidDeclarationId(declaration.id)) {
      error(
        '$path must be $_minDeclarationIdLength-$_maxDeclarationIdLength '
        'characters and use letters, numbers, underscore, dot, or dash.',
      );
      continue;
    }
    final lowered = declaration.id.toLowerCase();
    if (!lowered.startsWith(prefix) || lowered.length <= prefix.length) {
      error(
        '$path must be namespaced under this package: it has to start with '
        "'${manifest.id}.' and name something beyond it.",
      );
      continue;
    }
    if (!owned.add(lowered)) {
      error('$path repeats a declaration id already used in this manifest.');
    }
  }
  return owned;
}

void _validateWorldDeclaration(
  ModManifest manifest,
  ModWorldDeclaration world,
  String path,
  void Function(String) error,
) {
  _contributionText(world.name, '$path.name', 1, 128, error);
  _contributionText(world.description, '$path.description', 0, 1024, error);
  _validateTransitionList(world.transitions, '$path.transitions', true, error);

  final content = world.content;
  if (content != null) {
    _validateWorldContent(content, '$path.content', manifest, error);
  }

  final spawn = world.spawn;
  if (spawn != null) {
    if (spawn.kind != ModSpawnPolicy.authoredMarkerKind &&
        spawn.kind != ModSpawnPolicy.providerDefaultKind) {
      error('$path.spawn.kind must be authored-marker or provider-default.');
    } else if (spawn.kind == ModSpawnPolicy.authoredMarkerKind) {
      _contributionText(
        spawn.markerName,
        '$path.spawn.markerName',
        1,
        128,
        error,
      );
    } else if (spawn.markerName.isNotEmpty) {
      error(
        '$path.spawn.markerName only means something for an authored-marker '
        'spawn.',
      );
    }
  }

  // A world either names the gamemodes it consents to or consents to all
  // compatible ones. Saying both leaves the narrower list looking authoritative
  // when it is not.
  if (world.openToAnyCompatible == true && world.openTo.isNotEmpty) {
    error(
      '$path.openTo cannot be listed alongside openToAnyCompatible: the list '
      'would read as a limit it is not.',
    );
  }
  if (world.openTo.length > 32) {
    error('$path.openTo cannot contain more than 32 entries.');
  }
  for (final consent in world.openTo) {
    _validateDeclarationReference(manifest, consent, '$path.openTo', error);
  }
}

void _validateWorldContent(
  ModWorldContent content,
  String path,
  ModManifest manifest,
  void Function(String) error,
) {
  const kinds = {
    ModWorldContent.bundleKind,
    ModWorldContent.providerKind,
    ModWorldContent.gameSceneKind,
    ModWorldContent.discoveredKind,
  };
  if (!kinds.contains(content.kind)) {
    error('$path.kind must be bundle, provider, game-scene, or discovered.');
    return;
  }

  final required = switch (content.kind) {
    ModWorldContent.bundleKind => ['bundle', 'prefab'],
    ModWorldContent.gameSceneKind => ['sceneName'],
    _ => ['implementation'],
  };
  final present = <String>[
    if (content.bundle.isNotEmpty) 'bundle',
    if (content.prefab.isNotEmpty) 'prefab',
    if (content.sceneName.isNotEmpty) 'sceneName',
    if (content.implementation != null) 'implementation',
  ];
  for (final field in required.where((field) => !present.contains(field))) {
    error('$path of kind ${content.kind} requires $field.');
  }
  for (final field in present.where((field) => !required.contains(field))) {
    error('$path of kind ${content.kind} cannot also carry $field.');
  }

  _contributionText(content.prefab, '$path.prefab', 0, 512, error);
  _contributionText(content.sceneName, '$path.sceneName', 0, 128, error);
  if (content.bundle.isNotEmpty &&
      _portableManifestPathCollisionKey(content.bundle) == null) {
    error('$path.bundle must be a safe relative path inside the package.');
  }

  final implementation = content.implementation;
  if (implementation != null) {
    _validateImplementationBinding(
      manifest,
      implementation,
      '$path.implementation',
      error,
    );
  }
}

void _validateGamemodeDeclaration(
  ModManifest manifest,
  ModGamemodeDeclaration gamemode,
  String path,
  void Function(String) error,
) {
  _contributionText(gamemode.name, '$path.name', 1, 128, error);
  _contributionText(gamemode.description, '$path.description', 0, 1024, error);

  final implementation = gamemode.implementation;
  if (implementation != null) {
    _validateImplementationBinding(
      manifest,
      implementation,
      '$path.implementation',
      error,
    );
  }

  if (gamemode.sceneChangePolicy.isNotEmpty &&
      gamemode.sceneChangePolicy != ModGamemodeDeclaration.endSessionPolicy &&
      gamemode.sceneChangePolicy !=
          ModGamemodeDeclaration.keepControllerPolicy) {
    error('$path.sceneChangePolicy must be end-session or keep-controller.');
  }

  final requirements = gamemode.worldRequirements;
  if (requirements == null) {
    return;
  }
  if (requirements.transitions.isNotEmpty) {
    _validateTransitionList(
      requirements.transitions,
      '$path.worldRequirements.transitions',
      false,
      error,
    );
  }
  if (requirements.spawn.isNotEmpty &&
      requirements.spawn != ModSpawnPolicy.authoredMarkerKind &&
      requirements.spawn != ModWorldRequirements.anySpawn) {
    error('$path.worldRequirements.spawn must be authored-marker or any.');
  }
}

void _validateLaunchTargetDeclaration(
  ModManifest manifest,
  ModLaunchTargetDeclaration target,
  String path,
  Set<String> owned,
  Set<String> discovered,
  void Function(String) error,
) {
  _contributionText(target.title, '$path.title', 1, 128, error);
  _contributionText(target.description, '$path.description', 0, 1024, error);
  final sortKey = target.sortKey;
  if (sortKey != null && (sortKey < 0 || sortKey > 999)) {
    error('$path.sortKey must be between 0 and 999.');
  }
  if (target.transition.isNotEmpty &&
      !const {
        ModLaunchTargetDeclaration.autoTransition,
        ModLaunchTargetDeclaration.playerChoiceTransition,
        ModTransitions.sceneReplacement,
        ModTransitions.additiveArena,
      }.contains(target.transition)) {
    error(
      '$path.transition must be auto, player-choice, scene-replacement, or '
      'additive-arena.',
    );
  }

  _validateDeclarationReference(
    manifest,
    target.gamemode,
    '$path.gamemode',
    error,
  );
  if (_isLocalReference(manifest, target.gamemode) &&
      !manifest.contributions!.gamemodes.any(
        (item) => item.id.toLowerCase() == target.gamemode.toLowerCase(),
      )) {
    error(
      '$path.gamemode names an id inside this package that this manifest does '
      'not declare.',
    );
  }

  final policy = target.world;
  if (policy == null) {
    return;
  }
  if (!const {
    ModWorldPolicy.fixedPolicy,
    ModWorldPolicy.listPolicy,
    ModWorldPolicy.openPolicy,
  }.contains(policy.policy)) {
    error('$path.world.policy must be fixed, list, or open.');
  }

  _validateWorldReference(
    manifest,
    policy.defaultWorldId,
    '$path.world.default',
    owned,
    discovered,
    error,
  );
  if (policy.allow.length > 64) {
    error('$path.world.allow cannot contain more than 64 entries.');
  }
  for (final allowed in policy.allow) {
    _validateWorldReference(
      manifest,
      allowed,
      '$path.world.allow',
      owned,
      discovered,
      error,
    );
  }

  if (policy.policy == ModWorldPolicy.listPolicy) {
    if (policy.allow.isEmpty) {
      error('$path.world.allow is required by the list policy.');
    } else if (!policy.allow.any(
      (item) => item.toLowerCase() == policy.defaultWorldId.toLowerCase(),
    )) {
      error(
        '$path.world.default must be a member of allow, or the default is a '
        'world the policy does not admit.',
      );
    }
  } else if (policy.allow.isNotEmpty) {
    final tail = policy.policy == ModWorldPolicy.openPolicy
        ? ' plus any consenting world.'
        : ' and nothing else.';
    error(
      '$path.world.allow only means something for the list policy; '
      '${policy.policy} admits its default$tail',
    );
  }

  if (policy.policy == ModWorldPolicy.fixedPolicy &&
      policy.allowPlayerOverride == true) {
    error(
      '$path.world.allowPlayerOverride contradicts the fixed policy, which '
      'admits one world.',
    );
  }

  _validateLocalPairing(manifest, target, path, error);
}

/// R10, and only where it can actually be checked. When the world and the
/// gamemode are declared in different packages this manifest cannot see both
/// sides, so compatibility is the resolver's job; checking it here would pass
/// every first-party pairing without looking.
void _validateLocalPairing(
  ModManifest manifest,
  ModLaunchTargetDeclaration target,
  String path,
  void Function(String) error,
) {
  final contributions = manifest.contributions!;
  final defaultWorldId = target.world?.defaultWorldId.toLowerCase();
  final worlds = contributions.worlds
      .where((item) => item.id.toLowerCase() == defaultWorldId)
      .toList(growable: false);
  final gamemodes = contributions.gamemodes
      .where((item) => item.id.toLowerCase() == target.gamemode.toLowerCase())
      .toList(growable: false);
  if (worlds.isEmpty || gamemodes.isEmpty) {
    return;
  }
  final world = worlds.first;
  final gamemode = gamemodes.first;

  final requirements = gamemode.worldRequirements;
  if (requirements != null &&
      requirements.spawn == ModSpawnPolicy.authoredMarkerKind &&
      world.spawn != null &&
      world.spawn!.kind != ModSpawnPolicy.authoredMarkerKind) {
    error(
      '$path.world.default names a world with a provider-default spawn, but '
      '${gamemode.id} requires an authored marker.',
    );
  }

  final offered = requirements == null || requirements.transitions.isEmpty
      ? world.transitions
      : world.transitions
            .where(requirements.transitions.contains)
            .toList(growable: false);
  if (offered.isEmpty) {
    error(
      '$path.world.default names a world that shares no transition with '
      '${gamemode.id}.',
    );
  } else if (target.transition.isNotEmpty &&
      target.transition != ModLaunchTargetDeclaration.autoTransition &&
      target.transition != ModLaunchTargetDeclaration.playerChoiceTransition &&
      !offered.contains(target.transition)) {
    error(
      '$path.transition is not one the default world and ${gamemode.id} both '
      'offer.',
    );
  }
}
