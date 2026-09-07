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
      safeMode: _profileBoolean(json, 'safeMode'),
      extraArguments: _profileStrings(json, 'extraArguments'),
      environment: _profileStringMap(json, 'environment'),
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
    WorldSelection? worldSelection,
    LaunchSelection? launchSelection,
    this.revision = 0,
    this.backupMetadata = const {},
  }) : _worldSelection = worldSelection,
       _launchSelection = launchSelection;

  final String id;
  final String name;

  /// Whether mod enablement should come from the manager's durable state.
  /// Exact profiles may deliberately have an empty [enabledMods] set.
  final bool inheritManagerModState;
  final Set<String> enabledMods;
  final Map<String, String> selectedVersions;
  final Map<String, Object?> configMetadata;
  final LaunchSettings launchSettings;
  final int revision;
  final WorldSelection? _worldSelection;
  final LaunchSelection? _launchSelection;

  /// Legacy input retained while callers migrate; never runtime authority.
  WorldSelection get worldSelection =>
      _worldSelection ?? const WorldSelection();
  LaunchSelection get launchSelection =>
      _launchSelection ??
      (_worldSelection == null
          ? const LaunchSelection.mainMenu()
          : LaunchSelection.unresolvedLegacy({
              'worldSelection': _worldSelection.toJson(),
            }));
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
      backupMetadata: const {},
    );
  }

  factory LauncherProfile.fromJson(Map<String, Object?> json) {
    final enabledMods = Set<String>.unmodifiable(
      _profileStrings(json, 'enabledMods', packageIds: true),
    );
    final revision = json.containsKey('revision') ? json['revision'] : 0;
    if (revision is! int || revision < 0 || revision > 2147483647) {
      throw const FormatException(
        'Profile revision must be a safe nonnegative integer.',
      );
    }
    final selection = json.containsKey('launchSelection')
        ? LaunchSelection.fromJson(json['launchSelection'])
        : LaunchSelection.unresolvedLegacy({
            if (json.containsKey('worldSelection'))
              'worldSelection': json['worldSelection'],
          });
    return LauncherProfile(
      id: (json['id'] as String?) ?? 'default',
      name: (json['name'] as String?) ?? 'Default',
      inheritManagerModState: _profileBoolean(json, 'inheritManagerModState'),
      enabledMods: enabledMods,
      selectedVersions: _profileStringMap(
        json,
        'selectedVersions',
        packageIds: true,
      ),
      configMetadata: _objectMap(json['configMetadata']),
      launchSettings: LaunchSettings.fromJson(
        _profileObject(json, 'launchSettings'),
      ),
      revision: revision,
      launchSelection: selection,
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
    'revision': revision,
    'launchSelection': launchSelection.toJson(),
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
    LaunchSelection? launchSelection,
    int? revision,
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
      worldSelection: worldSelection ?? _worldSelection,
      launchSelection:
          launchSelection ??
          (worldSelection == null ? this.launchSelection : null),
      revision: revision ?? this.revision,
      backupMetadata: backupMetadata,
    );
  }
}
