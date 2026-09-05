part of '../models.dart';

/// Raw V6 shape validation happens before any constructor can default or coerce
/// a value. Semantic ownership, references and pairing remain separate checks.
void _contributionStructuralIssues(Object? value, List<String> issues) {
  final shape = _ContributionShape(issues);
  final fields = shape.object(
    value,
    'contributions',
    const {'worlds', 'gamemodes', 'launchTargets'},
    const {},
    nonEmpty: true,
  );
  if (fields == null) return;
  for (final item in shape.objects(fields, 'worlds', 'contributions', 64)) {
    _worldContributionShape(item.value, item.path, shape);
  }
  for (final item in shape.objects(fields, 'gamemodes', 'contributions', 16)) {
    _gamemodeContributionShape(item.value, item.path, shape);
  }
  for (final item in shape.objects(
    fields,
    'launchTargets',
    'contributions',
    64,
  )) {
    _launchTargetContributionShape(item.value, item.path, shape);
  }
}

void _worldContributionShape(
  Object? value,
  String path,
  _ContributionShape shape,
) {
  final fields = shape.object(
    value,
    path,
    const {
      'id',
      'name',
      'description',
      'content',
      'transitions',
      'spawn',
      'openTo',
      'openToAnyCompatible',
    },
    const {'id', 'name', 'content', 'transitions', 'spawn'},
  );
  if (fields == null) return;
  shape.id(fields, 'id', path);
  // Reserve a dot and a nonempty suffix inside the 96-character instance limit.
  if (fields['content'] case {'kind': ModWorldContent.discoveredKind}) {
    shape.text(fields, 'id', path, minimum: 4, maximum: 94);
  }
  shape.text(fields, 'name', path, minimum: 1, maximum: 128);
  shape.text(fields, 'description', path, maximum: 1024);
  shape.strings(
    fields,
    'transitions',
    path,
    minimum: 1,
    maximum: 2,
    choices: ModTransitions.byPrecedence,
  );
  shape.strings(fields, 'openTo', path, maximum: 32, declarationIds: true);
  shape.boolean(fields, 'openToAnyCompatible', path);
  if (fields['openToAnyCompatible'] == true && fields.containsKey('openTo')) {
    shape.error(
      '$path.openTo cannot be present with openToAnyCompatible true.',
    );
  }
  if (fields.containsKey('content')) {
    _worldContentShape(fields['content'], '$path.content', shape);
  }
  if (fields.containsKey('spawn')) {
    _spawnContributionShape(fields['spawn'], '$path.spawn', shape);
  }
}

void _worldContentShape(Object? value, String path, _ContributionShape shape) {
  final fields = shape.object(
    value,
    path,
    const {'kind', 'bundle', 'prefab', 'implementation', 'sceneName'},
    const {'kind'},
  );
  if (fields == null) return;
  shape.text(
    fields,
    'kind',
    path,
    choices: const {
      ModWorldContent.bundleKind,
      ModWorldContent.providerKind,
      ModWorldContent.gameSceneKind,
      ModWorldContent.discoveredKind,
    },
  );
  shape.path(fields, 'bundle', path);
  shape.text(fields, 'prefab', path, minimum: 1, maximum: 512);
  shape.text(fields, 'sceneName', path, minimum: 1, maximum: 128);
  if (fields.containsKey('implementation')) {
    _implementationContributionShape(
      fields['implementation'],
      '$path.implementation',
      shape,
    );
  }
  final required = switch (fields['kind']) {
    ModWorldContent.bundleKind => const {'bundle', 'prefab'},
    ModWorldContent.providerKind ||
    ModWorldContent.discoveredKind => const {'implementation'},
    ModWorldContent.gameSceneKind => const {'sceneName'},
    _ => null,
  };
  if (required != null) {
    shape.conditional(
      fields,
      path,
      required,
      const {
        'bundle',
        'prefab',
        'implementation',
        'sceneName',
      }.difference(required),
    );
  }
}

void _spawnContributionShape(
  Object? value,
  String path,
  _ContributionShape shape,
) {
  final fields = shape.object(
    value,
    path,
    const {'kind', 'markerName'},
    const {'kind'},
  );
  if (fields == null) return;
  shape.text(
    fields,
    'kind',
    path,
    choices: const {
      ModSpawnPolicy.authoredMarkerKind,
      ModSpawnPolicy.providerDefaultKind,
    },
  );
  shape.text(fields, 'markerName', path, minimum: 1, maximum: 128);
  if (fields['kind'] == ModSpawnPolicy.authoredMarkerKind) {
    shape.conditional(fields, path, const {'markerName'}, const {});
  } else if (fields['kind'] == ModSpawnPolicy.providerDefaultKind) {
    shape.conditional(fields, path, const {}, const {'markerName'});
  }
}

void _implementationContributionShape(
  Object? value,
  String path,
  _ContributionShape shape,
) {
  final fields = shape.object(
    value,
    path,
    const {'assembly', 'type'},
    const {'type'},
  );
  if (fields == null) return;
  shape.path(fields, 'assembly', path, dll: true);
  shape.text(
    fields,
    'type',
    path,
    minimum: 3,
    maximum: 512,
    accepts: _isValidTypeName,
    grammar: 'an ASCII namespace-qualified CLR type',
  );
}

void _gamemodeContributionShape(
  Object? value,
  String path,
  _ContributionShape shape,
) {
  final fields = shape.object(
    value,
    path,
    const {
      'id',
      'name',
      'description',
      'implementation',
      'worldRequirements',
      'sceneChangePolicy',
    },
    const {'id', 'name', 'implementation'},
  );
  if (fields == null) return;
  shape.id(fields, 'id', path);
  shape.text(fields, 'name', path, minimum: 1, maximum: 128);
  shape.text(fields, 'description', path, maximum: 1024);
  shape.text(
    fields,
    'sceneChangePolicy',
    path,
    choices: const {
      ModGamemodeDeclaration.endSessionPolicy,
      ModGamemodeDeclaration.keepControllerPolicy,
    },
  );
  if (fields.containsKey('implementation')) {
    _implementationContributionShape(
      fields['implementation'],
      '$path.implementation',
      shape,
    );
  }
  if (!fields.containsKey('worldRequirements')) return;
  final requirementsPath = '$path.worldRequirements';
  final requirements = shape.object(
    fields['worldRequirements'],
    requirementsPath,
    const {'transitions', 'spawn'},
    const {},
    nonEmpty: true,
  );
  if (requirements == null) return;
  shape.strings(
    requirements,
    'transitions',
    requirementsPath,
    minimum: 1,
    maximum: 2,
    choices: ModTransitions.byPrecedence,
  );
  shape.text(
    requirements,
    'spawn',
    requirementsPath,
    choices: const {
      ModSpawnPolicy.authoredMarkerKind,
      ModWorldRequirements.anySpawn,
    },
  );
}

void _launchTargetContributionShape(
  Object? value,
  String path,
  _ContributionShape shape,
) {
  final fields = shape.object(
    value,
    path,
    const {
      'id',
      'title',
      'description',
      'sortKey',
      'gamemode',
      'world',
      'transition',
    },
    const {'id', 'title', 'gamemode', 'world'},
  );
  if (fields == null) return;
  shape.id(fields, 'id', path);
  shape.id(fields, 'gamemode', path);
  shape.text(fields, 'title', path, minimum: 1, maximum: 128);
  shape.text(fields, 'description', path, maximum: 1024);
  shape.integer(fields, 'sortKey', path, 0, 999);
  shape.text(
    fields,
    'transition',
    path,
    choices: const {
      ModLaunchTargetDeclaration.autoTransition,
      ModLaunchTargetDeclaration.playerChoiceTransition,
      ModTransitions.sceneReplacement,
      ModTransitions.additiveArena,
    },
  );
  if (!fields.containsKey('world')) return;
  final policyPath = '$path.world';
  final policy = shape.object(
    fields['world'],
    policyPath,
    const {'policy', 'default', 'allow', 'allowPlayerOverride'},
    const {'policy', 'default'},
  );
  if (policy == null) return;
  shape.text(
    policy,
    'policy',
    policyPath,
    choices: const {
      ModWorldPolicy.fixedPolicy,
      ModWorldPolicy.listPolicy,
      ModWorldPolicy.openPolicy,
    },
  );
  shape.id(policy, 'default', policyPath);
  shape.strings(
    policy,
    'allow',
    policyPath,
    minimum: 1,
    maximum: 64,
    declarationIds: true,
  );
  shape.boolean(policy, 'allowPlayerOverride', policyPath);
  if (policy['policy'] == ModWorldPolicy.listPolicy) {
    shape.conditional(policy, policyPath, const {'allow'}, const {});
  } else {
    shape.conditional(policy, policyPath, const {}, const {'allow'});
  }
  if (policy['policy'] == ModWorldPolicy.fixedPolicy &&
      policy['allowPlayerOverride'] == true) {
    shape.error(
      '$policyPath.allowPlayerOverride must be false for fixed policy.',
    );
  }
}
