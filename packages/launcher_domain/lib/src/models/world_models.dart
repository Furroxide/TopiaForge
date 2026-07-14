part of '../models.dart';

class WorldDefinition {
  const WorldDefinition({
    required this.id,
    required this.name,
    this.description = '',
    this.sceneName = '',
    this.firstParty = false,
    this.supportsSceneReplacement = false,
    this.supportsAdditiveArena = true,
  });

  final String id;
  final String name;
  final String description;
  final String sceneName;
  final bool firstParty;
  final bool supportsSceneReplacement;
  final bool supportsAdditiveArena;

  /// The load modes this world can actually honour, derived from its capability flags. A world that supports
  /// only one mode gives the user no real choice (the runtime would silently override the other), so the UI
  /// uses this to lock the "Load mode" control instead of offering a mode the world cannot satisfy.
  Set<String> get supportedLoadModes {
    return {
      if (supportsSceneReplacement) WorldSelection.sceneReplacement,
      if (supportsAdditiveArena) WorldSelection.additiveArena,
    };
  }

  factory WorldDefinition.fromJson(Map<String, Object?> json) {
    return WorldDefinition(
      id: (json['id'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      description: (json['description'] as String?) ?? '',
      sceneName: (json['sceneName'] as String?) ?? '',
      firstParty: (json['firstParty'] as bool?) ?? false,
      supportsSceneReplacement:
          (json['supportsSceneReplacement'] as bool?) ?? false,
      supportsAdditiveArena: (json['supportsAdditiveArena'] as bool?) ?? true,
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    if (description.isNotEmpty) 'description': description,
    if (sceneName.isNotEmpty) 'sceneName': sceneName,
    if (firstParty) 'firstParty': true,
    if (supportsSceneReplacement) 'supportsSceneReplacement': true,
    'supportsAdditiveArena': supportsAdditiveArena,
  };
}

class GamemodeDefinition {
  const GamemodeDefinition({
    required this.id,
    required this.name,
    this.description = '',
  });

  final String id;
  final String name;
  final String description;

  factory GamemodeDefinition.fromJson(Map<String, Object?> json) {
    return GamemodeDefinition(
      id: (json['id'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      description: (json['description'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    if (description.isNotEmpty) 'description': description,
  };
}

class WorldSelection {
  const WorldSelection({
    this.worldId = WorldCatalog.openSandboxWorldId,
    this.gamemodeId = WorldCatalog.sandboxGamemodeId,
    this.loadMode = additiveArena,
    this.autoLoadOnStart = false,
  });

  static const additiveArena = 'additiveArena';
  static const sceneReplacement = 'sceneReplacement';

  /// The only load modes the runtime (C# WorldsConfig) understands. Any other value is meaningless to the mod
  /// and, untreated, would crash the load-mode dropdown (DropdownButtonFormField asserts on an unknown value).
  static const supportedLoadModes = {additiveArena, sceneReplacement};

  /// Clamps an arbitrary/persisted load-mode string to a value the runtime and UI both accept.
  static String normalizeLoadMode(String? value) =>
      supportedLoadModes.contains(value) ? value! : additiveArena;

  final String worldId;
  final String gamemodeId;
  final String loadMode;
  final bool autoLoadOnStart;

  bool get preferSceneReplacement => loadMode == sceneReplacement;

  factory WorldSelection.fromJson(Map<String, Object?> json) {
    return WorldSelection(
      worldId:
          (json['worldId'] as String?) ??
          (json['selectedWorldId'] as String?) ??
          WorldCatalog.openSandboxWorldId,
      gamemodeId:
          (json['gamemodeId'] as String?) ??
          (json['selectedGamemodeId'] as String?) ??
          WorldCatalog.sandboxGamemodeId,
      loadMode: normalizeLoadMode(json['loadMode'] as String?),
      autoLoadOnStart: (json['autoLoadOnStart'] as bool?) ?? false,
    );
  }

  Map<String, Object?> toJson() => {
    'worldId': worldId,
    'gamemodeId': gamemodeId,
    'loadMode': loadMode,
    'autoLoadOnStart': autoLoadOnStart,
  };

  Map<String, Object?> toRuntimeConfig() => {
    'selectedWorldId': worldId,
    'selectedGamemodeId': gamemodeId,
    'loadMode': loadMode,
    'autoLoadOnStart': autoLoadOnStart,
    'allowAdditiveFallback': true,
  };

  /// Applies launcher-owned selection keys without erasing runtime-owned or
  /// future fields from an existing `robotopia.worlds.json` object.
  Map<String, Object?> mergeRuntimeConfig(Map<String, Object?> existing) => {
    ...existing,
    ...toRuntimeConfig(),
  };

  WorldSelection copyWith({
    String? worldId,
    String? gamemodeId,
    String? loadMode,
    bool? autoLoadOnStart,
  }) {
    return WorldSelection(
      worldId: worldId ?? this.worldId,
      gamemodeId: gamemodeId ?? this.gamemodeId,
      loadMode: loadMode ?? this.loadMode,
      autoLoadOnStart: autoLoadOnStart ?? this.autoLoadOnStart,
    );
  }
}

class WorldCatalog {
  const WorldCatalog({required this.worlds, required this.gamemodes});

  static const openSandboxWorldId = 'robotopia.worlds.open_sandbox';
  static const sandboxGamemodeId = 'robotopia.worlds.sandbox';

  final List<WorldDefinition> worlds;
  final List<GamemodeDefinition> gamemodes;

  /// Clamps [requestedMode] to a load mode the world [worldId] can actually honour. The UI's load-mode
  /// control only clamps for display, so this is what keeps the *persisted/written* selection coherent:
  /// a world that supports a single mode (a checkpoint level is scene-replacement only; the open sandbox
  /// is additive only) snaps the mode to that one mode instead of carrying an incompatible value (e.g.
  /// additiveArena for a checkpoint level) into the runtime config. An unknown world keeps the normalized
  /// requested mode, since its capabilities are not known here.
  String reconcileLoadMode(String worldId, String? requestedMode) {
    final requested = WorldSelection.normalizeLoadMode(requestedMode);
    final match = worlds.where((world) => world.id == worldId);
    if (match.isEmpty) {
      return requested;
    }
    final supported = match.first.supportedLoadModes;
    if (supported.isEmpty || supported.contains(requested)) {
      return requested;
    }
    return supported.first;
  }

  factory WorldCatalog.fallback() {
    return const WorldCatalog(
      worlds: [
        WorldDefinition(
          id: openSandboxWorldId,
          name: 'Open Sandbox',
          description: 'Generated open-world sandbox arena.',
        ),
      ],
      gamemodes: [
        GamemodeDefinition(
          id: sandboxGamemodeId,
          name: 'Sandbox',
          description: 'Freeform world loading for creator mods.',
        ),
      ],
    );
  }

  factory WorldCatalog.fromJson(Map<String, Object?> json) {
    final worlds = (json['worlds'] as List? ?? const [])
        .whereType<Map>()
        .map((item) => WorldDefinition.fromJson(_objectMap(item)))
        .where((world) => world.id.isNotEmpty && world.name.isNotEmpty)
        .toList();
    final gamemodes = (json['gamemodes'] as List? ?? const [])
        .whereType<Map>()
        .map((item) => GamemodeDefinition.fromJson(_objectMap(item)))
        .where((mode) => mode.id.isNotEmpty && mode.name.isNotEmpty)
        .toList();

    // Backfill only the missing side from the built-in catalog rather than discarding both: a catalog with
    // real worlds but no gamemodes (or vice versa) keeps the valid side instead of collapsing to Open Sandbox.
    if (worlds.isEmpty && gamemodes.isEmpty) {
      return WorldCatalog.fallback();
    }
    final fallback = WorldCatalog.fallback();
    return WorldCatalog(
      worlds: worlds.isEmpty ? fallback.worlds : worlds,
      gamemodes: gamemodes.isEmpty ? fallback.gamemodes : gamemodes,
    );
  }
}
