part of '../models.dart';

/// What a schemaVersion 6 package contributes to the launch surface: the worlds
/// it owns, the gamemodes it implements, and the targets a player can pick.
///
/// Wrapped in one object rather than declared as top-level `worlds`/`gamemodes`
/// keys because `gamemodes` is already taken -- it is a live retired-field
/// sentinel both readers reject on sight. One extra object level sidesteps the
/// collision and costs nothing else.
///
/// V5 could express none of this. A `worldGamemodes` entry was an id, a name and
/// a description: no implementation owner, no world, no launch identity, so the
/// manifest and the code that actually ran could disagree and nothing noticed.
///
/// Mirrors `ModContributions` in `src/TopiaForge.ModManager.Core`. The two are
/// hand-written and only this side ever sees the JSON Schema, so the shared
/// fixtures under `tests/fixtures/gamemode-v6` are what keep them equal.
class ModContributions {
  const ModContributions({
    this.worlds = const [],
    this.gamemodes = const [],
    this.launchTargets = const [],
  });

  static bool isValidDeclarationId(String id) => _isValidDeclarationId(id);

  factory ModContributions.fromJson(Object? json) {
    final issues = <String>[];
    _contributionStructuralIssues(json, issues);
    if (issues.isNotEmpty) {
      throw FormatException('contributions is invalid: ${issues.join(' ')}');
    }
    final map = json is Map ? json : const <String, Object?>{};
    return ModContributions(
      worlds: _declarationList(map['worlds'], ModWorldDeclaration.fromJson),
      gamemodes: _declarationList(
        map['gamemodes'],
        ModGamemodeDeclaration.fromJson,
      ),
      launchTargets: _declarationList(
        map['launchTargets'],
        ModLaunchTargetDeclaration.fromJson,
      ),
    );
  }

  final List<ModWorldDeclaration> worlds;
  final List<ModGamemodeDeclaration> gamemodes;
  final List<ModLaunchTargetDeclaration> launchTargets;

  bool get isEmpty =>
      worlds.isEmpty && gamemodes.isEmpty && launchTargets.isEmpty;

  Map<String, Object?> toJson() => {
    if (worlds.isNotEmpty)
      'worlds': worlds.map((item) => item.toJson()).toList(),
    if (gamemodes.isNotEmpty)
      'gamemodes': gamemodes.map((item) => item.toJson()).toList(),
    if (launchTargets.isNotEmpty)
      'launchTargets': launchTargets.map((item) => item.toJson()).toList(),
  };
}

/// Names the type that implements a declaration, and the assembly it lives in.
///
/// An object rather than a bare type string so [assembly] can exist at all: a
/// bare string would weld every gamemode to the manifest's `entryAssembly`
/// inside a contract that is closed to new fields.
class ModImplementationBinding {
  const ModImplementationBinding({this.assembly = '', this.type = ''});

  factory ModImplementationBinding.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModImplementationBinding(
      assembly: (map['assembly'] as String?) ?? '',
      type: (map['type'] as String?) ?? '',
    );
  }

  /// Empty means this manifest's `entryAssembly`.
  final String assembly;
  final String type;

  Map<String, Object?> toJson() => {
    if (assembly.isNotEmpty) 'assembly': assembly,
    'type': type,
  };
}

/// How a world's content is obtained.
class ModWorldContent {
  const ModWorldContent({
    this.kind = '',
    this.bundle = '',
    this.prefab = '',
    this.implementation,
    this.sceneName = '',
  });

  factory ModWorldContent.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModWorldContent(
      kind: (map['kind'] as String?) ?? '',
      bundle: (map['bundle'] as String?) ?? '',
      prefab: (map['prefab'] as String?) ?? '',
      implementation: map.containsKey('implementation')
          ? ModImplementationBinding.fromJson(map['implementation'])
          : null,
      sceneName: (map['sceneName'] as String?) ?? '',
    );
  }

  static const bundleKind = 'bundle';
  static const providerKind = 'provider';
  static const gameSceneKind = 'game-scene';

  /// A family of worlds enumerated at runtime rather than declared one by one.
  /// The declaration's id is the family prefix; instances are `<id>.<slug>` and
  /// are never launchable on their own, so a stored selection cannot name
  /// content that has never existed.
  static const discoveredKind = 'discovered';

  final String kind;
  final String bundle;
  final String prefab;
  final ModImplementationBinding? implementation;
  final String sceneName;

  Map<String, Object?> toJson() => {
    'kind': kind,
    if (bundle.isNotEmpty) 'bundle': bundle,
    if (prefab.isNotEmpty) 'prefab': prefab,
    if (implementation != null) 'implementation': implementation!.toJson(),
    if (sceneName.isNotEmpty) 'sceneName': sceneName,
  };
}

/// Where the player starts. Deliberately not a transform: V5 declares no numeric
/// fields at all, and a spawn point drifting in the last bits is the bug nobody
/// attributes to the manifest.
class ModSpawnPolicy {
  const ModSpawnPolicy({this.kind = '', this.markerName = ''});

  factory ModSpawnPolicy.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModSpawnPolicy(
      kind: (map['kind'] as String?) ?? '',
      markerName: (map['markerName'] as String?) ?? '',
    );
  }

  static const authoredMarkerKind = 'authored-marker';
  static const providerDefaultKind = 'provider-default';

  final String kind;
  final String markerName;

  Map<String, Object?> toJson() => {
    'kind': kind,
    if (markerName.isNotEmpty) 'markerName': markerName,
  };
}

/// One world this package owns.
class ModWorldDeclaration {
  const ModWorldDeclaration({
    this.id = '',
    this.name = '',
    String? description,
    this.content,
    this.transitions = const [],
    this.spawn,
    List<String>? openTo,
    this.openToAnyCompatible,
  }) : _description = description,
       _openTo = openTo;

  factory ModWorldDeclaration.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModWorldDeclaration(
      id: (map['id'] as String?) ?? '',
      name: (map['name'] as String?) ?? '',
      description: map['description'] as String?,
      content: map.containsKey('content')
          ? ModWorldContent.fromJson(map['content'])
          : null,
      transitions: _contributionStrings(map['transitions']),
      spawn: map.containsKey('spawn')
          ? ModSpawnPolicy.fromJson(map['spawn'])
          : null,
      openTo: map.containsKey('openTo')
          ? _contributionStrings(map['openTo'])
          : null,
      openToAnyCompatible: map['openToAnyCompatible'] as bool?,
    );
  }

  final String id;
  final String name;
  final String? _description;

  String get description => _description ?? '';
  bool get hasDescription => _description != null;
  final ModWorldContent? content;
  final List<String> transitions;
  final ModSpawnPolicy? spawn;

  /// Gamemodes this world agrees to be paired with by an `open` policy. Consent
  /// is scoped to that one policy on purpose: requiring it for every pairing
  /// would make a world's package depend on the gamemodes that use it, and the
  /// first-party graph already runs the other way.
  final List<String>? _openTo;

  List<String> get openTo => _openTo ?? const [];
  bool get hasOpenTo => _openTo != null;

  /// Null means absent, which is not the same as an explicit false.
  final bool? openToAnyCompatible;

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    if (hasDescription) 'description': description,
    if (content != null) 'content': content!.toJson(),
    'transitions': transitions,
    if (spawn != null) 'spawn': spawn!.toJson(),
    if (hasOpenTo) 'openTo': openTo,
    if (openToAnyCompatible != null) 'openToAnyCompatible': openToAnyCompatible,
  };
}

/// What a gamemode needs of a world. Absent entirely means no requirement; an
/// empty object is rejected, because absent already means that and the two must
/// stay distinguishable.
class ModWorldRequirements {
  const ModWorldRequirements({this.transitions = const [], this.spawn = ''});

  factory ModWorldRequirements.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModWorldRequirements(
      transitions: _contributionStrings(map['transitions']),
      spawn: (map['spawn'] as String?) ?? '',
    );
  }

  static const anySpawn = 'any';

  final List<String> transitions;
  final String spawn;

  Map<String, Object?> toJson() => {
    if (transitions.isNotEmpty) 'transitions': transitions,
    if (spawn.isNotEmpty) 'spawn': spawn,
  };
}

/// One gamemode this package implements.
class ModGamemodeDeclaration {
  const ModGamemodeDeclaration({
    this.id = '',
    this.name = '',
    String? description,
    this.implementation,
    this.worldRequirements,
    this.sceneChangePolicy = '',
  }) : _description = description;

  factory ModGamemodeDeclaration.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModGamemodeDeclaration(
      id: (map['id'] as String?) ?? '',
      name: (map['name'] as String?) ?? '',
      description: map['description'] as String?,
      implementation: map.containsKey('implementation')
          ? ModImplementationBinding.fromJson(map['implementation'])
          : null,
      worldRequirements: map.containsKey('worldRequirements')
          ? ModWorldRequirements.fromJson(map['worldRequirements'])
          : null,
      sceneChangePolicy: (map['sceneChangePolicy'] as String?) ?? '',
    );
  }

  /// A scene change ends the session.
  static const endSessionPolicy = 'end-session';

  /// A scene change leaves the running controller alone. The default.
  static const keepControllerPolicy = 'keep-controller';

  final String id;
  final String name;
  final String? _description;

  String get description => _description ?? '';
  bool get hasDescription => _description != null;
  final ModImplementationBinding? implementation;
  final ModWorldRequirements? worldRequirements;
  final String sceneChangePolicy;

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    if (hasDescription) 'description': description,
    if (implementation != null) 'implementation': implementation!.toJson(),
    if (worldRequirements != null)
      'worldRequirements': worldRequirements!.toJson(),
    if (sceneChangePolicy.isNotEmpty) 'sceneChangePolicy': sceneChangePolicy,
  };
}

/// Which worlds a launch target admits.
class ModWorldPolicy {
  const ModWorldPolicy({
    this.policy = '',
    this.defaultWorldId = '',
    this.allow = const [],
    this.allowPlayerOverride,
  });

  factory ModWorldPolicy.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModWorldPolicy(
      policy: (map['policy'] as String?) ?? '',
      defaultWorldId: (map['default'] as String?) ?? '',
      allow: _contributionStrings(map['allow']),
      allowPlayerOverride: map['allowPlayerOverride'] as bool?,
    );
  }

  /// Only [defaultWorldId].
  static const fixedPolicy = 'fixed';

  /// [defaultWorldId] plus [allow], with no world-side consent.
  static const listPolicy = 'list';

  /// [defaultWorldId] plus any profile world that meets the gamemode's
  /// requirements and consents to the pairing itself.
  static const openPolicy = 'open';

  final String policy;

  /// Serialized as `default`, which is a reserved word in Dart.
  final String defaultWorldId;
  final List<String> allow;

  /// Null means absent, which is not the same as an explicit false.
  final bool? allowPlayerOverride;

  Map<String, Object?> toJson() => {
    'policy': policy,
    'default': defaultWorldId,
    if (allow.isNotEmpty) 'allow': allow,
    if (allowPlayerOverride != null) 'allowPlayerOverride': allowPlayerOverride,
  };
}

/// What the player picks. Menus, Home, Setup and the CLI all select one of
/// these, so its identity is user-facing and outlives any one menu.
class ModLaunchTargetDeclaration {
  const ModLaunchTargetDeclaration({
    this.id = '',
    this.title = '',
    String? description,
    this.sortKey,
    this.gamemode = '',
    this.world,
    this.transition = '',
  }) : _description = description;

  factory ModLaunchTargetDeclaration.fromJson(Object? json) {
    final map = json is Map ? json : const <String, Object?>{};
    return ModLaunchTargetDeclaration(
      id: (map['id'] as String?) ?? '',
      title: (map['title'] as String?) ?? '',
      description: map['description'] as String?,
      sortKey: _contributionSortKey(map['sortKey']),
      gamemode: (map['gamemode'] as String?) ?? '',
      world: map.containsKey('world')
          ? ModWorldPolicy.fromJson(map['world'])
          : null,
      transition: (map['transition'] as String?) ?? '',
    );
  }

  /// Take the highest-precedence transition both sides allow. Scene replacement
  /// outranks the additive arena, and the precedence is fixed so a world
  /// offering both is never ambiguous.
  static const autoTransition = 'auto';

  /// Offer the whole intersection to the player instead of choosing.
  static const playerChoiceTransition = 'player-choice';

  final String id;
  final String title;
  final String? _description;

  String get description => _description ?? '';
  bool get hasDescription => _description != null;

  /// Null means absent, which is not the same as an explicit 0.
  final int? sortKey;
  final String gamemode;
  final ModWorldPolicy? world;
  final String transition;

  Map<String, Object?> toJson() => {
    'id': id,
    'title': title,
    if (hasDescription) 'description': description,
    if (sortKey != null) 'sortKey': sortKey,
    'gamemode': gamemode,
    if (world != null) 'world': world!.toJson(),
    if (transition.isNotEmpty) 'transition': transition,
  };
}

/// The two ways a world can be entered.
abstract final class ModTransitions {
  static const sceneReplacement = 'scene-replacement';
  static const additiveArena = 'additive-arena';

  /// Most-preferred first. `auto` takes the first member of this order that both
  /// the world and the gamemode allow, which is what makes the choice
  /// deterministic for a world that supports both -- and one ships today.
  static const byPrecedence = [sceneReplacement, additiveArena];
}

List<T> _declarationList<T>(Object? value, T Function(Object?) read) {
  if (value is! List) {
    return const [];
  }
  return List.unmodifiable(value.map(read));
}

List<String> _contributionStrings(Object? value) => value == null
    ? const []
    : List<String>.unmodifiable((value as List).cast<String>());

int? _contributionSortKey(Object? value) {
  if (value == null) return null;
  if (value is! num ||
      !value.isFinite ||
      value != value.truncateToDouble() ||
      value < 0 ||
      value > 999) {
    throw const FormatException(
      'contributions sortKey must be an integer from 0 to 999.',
    );
  }
  return value.toInt();
}
