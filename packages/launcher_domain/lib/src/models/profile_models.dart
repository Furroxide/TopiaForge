part of '../models.dart';

class LaunchSettings {
  const LaunchSettings({
    this.safeMode = false,
    this.extraArguments = const [],
    this.environment = const {},
  });

  final bool safeMode;
  final List<String> extraArguments;
  final Map<String, String> environment;

  factory LaunchSettings.fromJson(Map<String, Object?> json) {
    return LaunchSettings(
      safeMode: (json['safeMode'] as bool?) ?? false,
      extraArguments: _stringList(json['extraArguments']),
      environment: _stringMap(json['environment']),
    );
  }

  Map<String, Object?> toJson() => {
    'safeMode': safeMode,
    if (extraArguments.isNotEmpty) 'extraArguments': extraArguments,
    if (environment.isNotEmpty) 'environment': environment,
  };

  LaunchSettings copyWith({
    bool? safeMode,
    List<String>? extraArguments,
    Map<String, String>? environment,
  }) {
    return LaunchSettings(
      safeMode: safeMode ?? this.safeMode,
      extraArguments: extraArguments ?? this.extraArguments,
      environment: environment ?? this.environment,
    );
  }
}

class LauncherProfile {
  const LauncherProfile({
    required this.id,
    required this.name,
    this.inheritManagerModState = false,
    this.enabledMods = const {},
    this.selectedVersions = const {},
    this.configMetadata = const {},
    this.launchSettings = const LaunchSettings(),
    this.worldSelection = const WorldSelection(),
    this.backupMetadata = const {},
  });

  final String id;
  final String name;

  /// Whether mod enablement should come from the manager's durable state.
  /// Exact profiles may deliberately have an empty [enabledMods] set.
  final bool inheritManagerModState;
  final Set<String> enabledMods;
  final Map<String, String> selectedVersions;
  final Map<String, Object?> configMetadata;
  final LaunchSettings launchSettings;
  final WorldSelection worldSelection;
  final Map<String, Object?> backupMetadata;

  factory LauncherProfile.defaultProfile() {
    return LauncherProfile(
      id: 'default',
      name: 'Default',
      inheritManagerModState: true,
      enabledMods: const {},
      selectedVersions: const {},
      configMetadata: const {},
      launchSettings: const LaunchSettings(),
      worldSelection: const WorldSelection(),
      backupMetadata: const {},
    );
  }

  factory LauncherProfile.fromJson(Map<String, Object?> json) {
    final enabledMods = _stringList(json['enabledMods']).toSet();
    return LauncherProfile(
      id: (json['id'] as String?) ?? 'default',
      name: (json['name'] as String?) ?? 'Default',
      inheritManagerModState: json['inheritManagerModState'] == true,
      enabledMods: enabledMods,
      selectedVersions: _stringMap(json['selectedVersions']),
      configMetadata: _objectMap(json['configMetadata']),
      launchSettings: LaunchSettings.fromJson(
        _objectMap(json['launchSettings']),
      ),
      worldSelection: WorldSelection.fromJson(
        _objectMap(json['worldSelection']),
      ),
      backupMetadata: _objectMap(json['backupMetadata']),
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    'inheritManagerModState': inheritManagerModState,
    'enabledMods': enabledMods.toList()..sort(),
    'selectedVersions': selectedVersions,
    if (configMetadata.isNotEmpty) 'configMetadata': configMetadata,
    'launchSettings': launchSettings.toJson(),
    'worldSelection': worldSelection.toJson(),
    if (backupMetadata.isNotEmpty) 'backupMetadata': backupMetadata,
  };

  LauncherProfile copyWith({
    String? id,
    String? name,
    bool? inheritManagerModState,
    Set<String>? enabledMods,
    Map<String, String>? selectedVersions,
    LaunchSettings? launchSettings,
    WorldSelection? worldSelection,
  }) {
    return LauncherProfile(
      id: id ?? this.id,
      name: name ?? this.name,
      inheritManagerModState:
          inheritManagerModState ?? this.inheritManagerModState,
      enabledMods: enabledMods ?? this.enabledMods,
      selectedVersions: selectedVersions ?? this.selectedVersions,
      configMetadata: configMetadata,
      launchSettings: launchSettings ?? this.launchSettings,
      worldSelection: worldSelection ?? this.worldSelection,
      backupMetadata: backupMetadata,
    );
  }
}

/// Immutable, process-scoped mod selection consumed by the runtime loader.
///
/// This is deliberately separate from manager state: applying a profile must
/// never rewrite the user's durable enablement or selected-version choices.
class ProfileLaunchConfiguration {
  ProfileLaunchConfiguration._({
    required this.profileId,
    required this.safeMode,
    required this.inheritManagerModState,
    required this.enabledMods,
    required this.selectedVersions,
    required this.worldLaunch,
  });

  static const int schemaVersion = 3;
  static const String environmentVariable = 'TOPIAFORGE_LAUNCH_PROFILE';

  final String profileId;
  final bool safeMode;
  final bool inheritManagerModState;
  final Set<String> enabledMods;
  final Map<String, String> selectedVersions;

  /// What this run should start: the chosen game mode, or the game's ordinary menu.
  ///
  /// This rides the launch profile because the profile is the only channel that actually reaches the
  /// runtime: it is validated, written into the manager's staging directory, read once and deleted.
  /// The previous route — merging the selection into the Worlds mod's own config file — never worked,
  /// because that document is a `{schemaVersion, value}` envelope owned by the mod and the launcher
  /// wrote its keys beside `value` rather than inside it. The mod never read them and its next save
  /// deleted them, so every gamemode the player picked was silently thrown away.
  ///
  /// Always present. The manager remembers a selection of its own, so an omitted instruction means
  /// "started without the launcher, use what you remember" — which is not what a player who picked
  /// None on Home asked for.
  final WorldSelection worldLaunch;

  factory ProfileLaunchConfiguration.fromProfile(LauncherProfile profile) {
    final profileId = profile.id;
    if (!_profileIdPattern.hasMatch(profileId)) {
      throw FormatException(
        'Profile id must contain 1-128 letters, numbers, dots, dashes, or '
        'underscores, and start with a letter or number.',
      );
    }

    final seenIds = <String>{};
    final selection = profile.worldSelection;
    if (!_isValidDeclarationId(selection.worldId)) {
      throw const FormatException(
        'Profile worldId must use the safe TopiaForge declaration id format.',
      );
    }
    if (!_isValidDeclarationId(selection.gamemodeId)) {
      throw const FormatException(
        'Profile gamemodeId must use the safe TopiaForge declaration id format.',
      );
    }
    final enabledMods = <String>[];
    for (final id in profile.enabledMods) {
      if (!ModManifest.isValidId(id) || !seenIds.add(id.toLowerCase())) {
        throw FormatException('Profile contains an invalid mod id: $id.');
      }
      enabledMods.add(id);
    }
    enabledMods.sort(_compareModIds);

    seenIds.clear();
    final selectedEntries = profile.selectedVersions.entries.toList();
    for (final entry in selectedEntries) {
      if (!ModManifest.isValidId(entry.key) ||
          !seenIds.add(entry.key.toLowerCase())) {
        throw FormatException(
          'Profile contains an invalid selected-version id: ${entry.key}.',
        );
      }
      if (SemanticVersion.tryParse(entry.value) == null) {
        throw FormatException(
          'Profile selects an invalid version for ${entry.key}: '
          '${entry.value}.',
        );
      }
    }
    selectedEntries.sort((a, b) => _compareModIds(a.key, b.key));

    return ProfileLaunchConfiguration._(
      profileId: profileId,
      safeMode: profile.launchSettings.safeMode,
      inheritManagerModState: profile.inheritManagerModState,
      enabledMods: Set.unmodifiable(enabledMods),
      selectedVersions: Map.unmodifiable({
        for (final entry in selectedEntries) entry.key: entry.value,
      }),
      worldLaunch: selection,
    );
  }

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'profileId': profileId,
    'safeMode': safeMode,
    'inheritManagerModState': inheritManagerModState,
    'enabledMods': enabledMods.toList(growable: false),
    'selectedVersions': selectedVersions,
    'worldLaunch': worldLaunch.toLaunchIntentJson(),
  };

  static int _compareModIds(String left, String right) {
    final folded = left.toLowerCase().compareTo(right.toLowerCase());
    return folded != 0 ? folded : left.compareTo(right);
  }
}

final _profileIdPattern = RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$');
