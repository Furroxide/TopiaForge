part of '../models.dart';

/// Closed key sets and required keys for the V6 `contributions` object, at every
/// nesting level.
///
/// Mirrors `ValidateContributionsObject` in
/// `src/TopiaForge.ModManager.Core/ModManifestJson.Contributions.cs`. Written out
/// twice on purpose: the C# manager never reads a JSON Schema -- nothing under
/// `src/` mentions `topiaforge.mod.schema.json` -- so anything only the schema
/// says is a rule the manager does not have.
///
/// Structure only. What the values *mean* (ownership, references, policy
/// coherence) is the contribution validator's job; these are the rules that
/// decide whether there is a shape to interpret at all.
void _contributionStructuralIssues(Object? value, List<String> issues) {
  if (value == null) {
    return;
  }
  if (value is! Map) {
    issues.add('contributions must be an object.');
    return;
  }

  _closedContributionObject(
    'contributions',
    value,
    const {'worlds', 'gamemodes', 'launchTargets'},
    const {},
    issues,
    requireAtLeastOne: true,
  );

  for (final world in _contributionItems(
    value['worlds'],
    'contributions.worlds',
    issues,
  )) {
    _closedContributionObject(
      world.path,
      world.value,
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
      issues,
    );
    if (world.value is! Map) {
      continue;
    }
    final fields = world.value as Map;
    _nestedContributionObject(
      fields,
      'content',
      '${world.path}.content',
      const {'kind', 'bundle', 'prefab', 'implementation', 'sceneName'},
      const {'kind'},
      issues,
    );
    final content = fields['content'];
    if (content is Map) {
      _nestedContributionObject(
        content,
        'implementation',
        '${world.path}.content.implementation',
        const {'assembly', 'type'},
        const {'type'},
        issues,
      );
    }
    _nestedContributionObject(
      fields,
      'spawn',
      '${world.path}.spawn',
      const {'kind', 'markerName'},
      const {'kind'},
      issues,
    );
  }

  for (final gamemode in _contributionItems(
    value['gamemodes'],
    'contributions.gamemodes',
    issues,
  )) {
    _closedContributionObject(
      gamemode.path,
      gamemode.value,
      const {
        'id',
        'name',
        'description',
        'implementation',
        'worldRequirements',
        'sceneChangePolicy',
      },
      const {'id', 'name', 'implementation'},
      issues,
    );
    if (gamemode.value is! Map) {
      continue;
    }
    final fields = gamemode.value as Map;
    _nestedContributionObject(
      fields,
      'implementation',
      '${gamemode.path}.implementation',
      const {'assembly', 'type'},
      const {'type'},
      issues,
    );
    // An empty worldRequirements is rejected rather than read as "no
    // requirement", because absent already means that and the two must stay
    // distinguishable.
    _nestedContributionObject(
      fields,
      'worldRequirements',
      '${gamemode.path}.worldRequirements',
      const {'transitions', 'spawn'},
      const {},
      issues,
      requireAtLeastOne: true,
    );
  }

  for (final target in _contributionItems(
    value['launchTargets'],
    'contributions.launchTargets',
    issues,
  )) {
    _closedContributionObject(
      target.path,
      target.value,
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
      issues,
    );
    if (target.value is! Map) {
      continue;
    }
    _nestedContributionObject(
      target.value as Map,
      'world',
      '${target.path}.world',
      const {'policy', 'default', 'allow', 'allowPlayerOverride'},
      const {'policy', 'default'},
      issues,
    );
  }
}

void _closedContributionObject(
  String path,
  Object? value,
  Set<String> allowed,
  Set<String> required,
  List<String> issues, {
  bool requireAtLeastOne = false,
}) {
  if (value is! Map) {
    issues.add('$path must be an object.');
    return;
  }
  final keys = value.keys.map((key) => key.toString()).toSet();
  if (requireAtLeastOne && keys.isEmpty) {
    issues.add('$path must contain at least one property.');
  }
  for (final key in keys) {
    if (!allowed.contains(key)) {
      issues.add('$path contains unknown field $key.');
    }
  }
  for (final key in required) {
    if (!keys.contains(key)) {
      issues.add('$path is missing required field $key.');
    }
  }
}

void _nestedContributionObject(
  Map<Object?, Object?> parent,
  String name,
  String path,
  Set<String> allowed,
  Set<String> required,
  List<String> issues, {
  bool requireAtLeastOne = false,
}) {
  if (!parent.containsKey(name)) {
    return;
  }
  _closedContributionObject(
    path,
    parent[name],
    allowed,
    required,
    issues,
    requireAtLeastOne: requireAtLeastOne,
  );
}

Iterable<_ContributionItem> _contributionItems(
  Object? value,
  String path,
  List<String> issues,
) sync* {
  if (value == null) {
    return;
  }
  if (value is! List) {
    issues.add('$path must be an array.');
    return;
  }
  // An empty declaration array is a mistake, not a contribution: a package that
  // wrote "worlds": [] meant to declare a world.
  if (value.isEmpty) {
    issues.add('$path must declare at least one entry or be omitted.');
    return;
  }
  for (var index = 0; index < value.length; index++) {
    yield _ContributionItem('$path[$index]', value[index]);
  }
}

class _ContributionItem {
  const _ContributionItem(this.path, this.value);

  final String path;
  final Object? value;
}
