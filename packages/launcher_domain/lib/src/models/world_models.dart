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

/// One launchable entry as the Worlds provider publishes it. A gamemode on its own does not say
/// *where* it should start; the entry carries the world its author intended, which is what "play this
/// gamemode" needs.
class GamemodeMenuEntry {
  const GamemodeMenuEntry({
    required this.id,
    required this.title,
    required this.gamemodeId,
    this.description = '',
    this.worldId = '',
  });

  final String id;
  final String title;
  final String gamemodeId;
  final String description;
  final String worldId;

  factory GamemodeMenuEntry.fromJson(Map<String, Object?> json) {
    return GamemodeMenuEntry(
      id: (json['id'] as String?) ?? '',
      title: (json['title'] as String?) ?? '',
      gamemodeId: (json['gamemodeId'] as String?) ?? '',
      description: (json['description'] as String?) ?? '',
      worldId: (json['worldId'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'title': title,
    'gamemodeId': gamemodeId,
    if (description.isNotEmpty) 'description': description,
    if (worldId.isNotEmpty) 'worldId': worldId,
  };
}

class WorldSelection {
  const WorldSelection({
    this.worldId = WorldCatalog.openSandboxWorldId,
    this.gamemodeId = WorldCatalog.sandboxGamemodeId,
    this.loadMode = additiveArena,
    this.launchIntoGamemode = false,
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

  /// Whether pressing Launch starts [gamemodeId] instead of booting the game normally. This is the
  /// Home screen's gamemode picker: "None" leaves it false, which is what every profile that predates
  /// the picker does, so an ordinary launch still lands on the game's own menu.
  final bool launchIntoGamemode;

  bool get preferSceneReplacement => loadMode == sceneReplacement;

  factory WorldSelection.fromJson(Map<String, Object?> json) {
    final worldId =
        (json['worldId'] as String?) ?? WorldCatalog.openSandboxWorldId;
    final gamemodeId =
        (json['gamemodeId'] as String?) ?? WorldCatalog.sandboxGamemodeId;
    // Declaration ids, not package ids: a launch target is namespaced under its
    // package, so it uses the wider 96-character grammar. Validating at the
    // package width here would reject a target the manifest contract calls
    // legal, and the intent this selection writes would never reach the game.
    if (!_isValidDeclarationId(worldId)) {
      throw const FormatException(
        'World selection worldId must use the safe TopiaForge declaration id '
        'format.',
      );
    }
    if (!_isValidDeclarationId(gamemodeId)) {
      throw const FormatException(
        'World selection gamemodeId must use the safe TopiaForge declaration '
        'id format.',
      );
    }
    return WorldSelection(
      worldId: worldId,
      gamemodeId: gamemodeId,
      loadMode: normalizeLoadMode(json['loadMode'] as String?),
      launchIntoGamemode: (json['launchIntoGamemode'] as bool?) ?? false,
    );
  }

  Map<String, Object?> toJson() => {
    'worldId': worldId,
    'gamemodeId': gamemodeId,
    'loadMode': loadMode,
    'launchIntoGamemode': launchIntoGamemode,
  };

  /// Start this game mode for one run.
  static const launchTargetCommand = 'launch-target';

  /// Boot to the game's own menu for one run, whatever the manager remembers.
  static const mainMenuCommand = 'main-menu';

  /// The one-shot instruction carried on the launch profile. The launcher no longer writes into the
  /// Worlds mod's own config file at all: that file belongs to the mod, and two writers merging into
  /// one document is exactly what used to discard the player's choice without a trace.
  ///
  /// "Play normally" is sent as a command rather than as silence. The manager also remembers a
  /// selection, edited from its in-game overlay, and it cannot tell "the launcher said nothing" from
  /// "the launcher asked for the ordinary menu" unless we say which one we mean.
  Map<String, Object?> toLaunchIntentJson() => launchIntoGamemode
      ? {
          'command': launchTargetCommand,
          'worldId': worldId,
          'gamemodeId': gamemodeId,
          'loadMode': loadMode,
          'allowAdditiveFallback': true,
        }
      : {'command': mainMenuCommand};

  WorldSelection copyWith({
    String? worldId,
    String? gamemodeId,
    String? loadMode,
    bool? launchIntoGamemode,
  }) {
    return WorldSelection(
      worldId: worldId ?? this.worldId,
      gamemodeId: gamemodeId ?? this.gamemodeId,
      loadMode: loadMode ?? this.loadMode,
      launchIntoGamemode: launchIntoGamemode ?? this.launchIntoGamemode,
    );
  }
}

class WorldCatalog {
  const WorldCatalog({
    required this.worlds,
    required this.gamemodes,
    this.menuEntries = const [],
  });

  static const openSandboxWorldId =
      'io.github.furroxide.topiaforge.worlds.open_sandbox';
  static const sandboxGamemodeId =
      'io.github.furroxide.topiaforge.worlds.sandbox';

  /// The Worlds provider's mod id. Its published catalog lives under the manager data directory,
  /// which is keyed by the **raw** mod id -- unlike the config directory, whose file stem shortens
  /// `io.github.furroxide.topiaforge.` to `topiaforge.`. Reading the catalog from the shortened name
  /// is why the launcher never found it and silently fell back to a one-world catalog.
  static const worldsModId = 'io.github.furroxide.topiaforge.worlds';

  final List<WorldDefinition> worlds;
  final List<GamemodeDefinition> gamemodes;
  final List<GamemodeMenuEntry> menuEntries;

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

  /// The menu entry that launches [gamemodeId], when the provider published one.
  GamemodeMenuEntry? menuEntryFor(String gamemodeId) {
    for (final entry in menuEntries) {
      if (entry.gamemodeId == gamemodeId) {
        return entry;
      }
    }
    return null;
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
        .where(
          (world) =>
              _isValidDeclarationId(world.id) && world.name.trim().isNotEmpty,
        )
        .toList();
    final gamemodes = (json['gamemodes'] as List? ?? const [])
        .whereType<Map>()
        .map((item) => GamemodeDefinition.fromJson(_objectMap(item)))
        .where(
          (mode) =>
              _isValidDeclarationId(mode.id) && mode.name.trim().isNotEmpty,
        )
        .toList();

    final menuEntries = (json['menuEntries'] as List? ?? const [])
        .whereType<Map>()
        .map((item) => GamemodeMenuEntry.fromJson(_objectMap(item)))
        .where(
          (entry) =>
              _isValidDeclarationId(entry.id) &&
              _isValidDeclarationId(entry.gamemodeId) &&
              (entry.worldId.isEmpty || _isValidDeclarationId(entry.worldId)) &&
              entry.title.trim().isNotEmpty,
        )
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
      menuEntries: menuEntries,
    );
  }
}
