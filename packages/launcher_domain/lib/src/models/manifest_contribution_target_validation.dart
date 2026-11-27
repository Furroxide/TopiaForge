part of '../models.dart';

void _validateLaunchTargetDeclaration(
  ModManifest manifest,
  ModLaunchTargetDeclaration target,
  String path,
  Set<String> ownedWorlds,
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
    ownedWorlds,
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
      ownedWorlds,
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
  if (!_isLocalReference(manifest, defaultWorldId ?? '') ||
      !_isLocalReference(manifest, target.gamemode)) {
    return;
  }
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
