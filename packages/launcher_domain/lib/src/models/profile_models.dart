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

  LaunchSettings copyWith({bool? safeMode, List<String>? extraArguments}) {
    return LaunchSettings(
      safeMode: safeMode ?? this.safeMode,
      extraArguments: extraArguments ?? this.extraArguments,
      environment: environment,
    );
  }
}

class LauncherProfile {
  const LauncherProfile({
    required this.id,
    required this.name,
    this.enabledMods = const {},
    this.selectedVersions = const {},
    this.configMetadata = const {},
    this.launchSettings = const LaunchSettings(),
    this.worldSelection = const WorldSelection(),
    this.backupMetadata = const {},
  });

  final String id;
  final String name;
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
      enabledMods: const {},
      selectedVersions: const {},
      configMetadata: const {},
      launchSettings: const LaunchSettings(),
      worldSelection: const WorldSelection(),
      backupMetadata: const {},
    );
  }

  factory LauncherProfile.fromJson(Map<String, Object?> json) {
    return LauncherProfile(
      id: (json['id'] as String?) ?? 'default',
      name: (json['name'] as String?) ?? 'Default',
      enabledMods: _stringList(json['enabledMods']).toSet(),
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
    Set<String>? enabledMods,
    Map<String, String>? selectedVersions,
    LaunchSettings? launchSettings,
    WorldSelection? worldSelection,
  }) {
    return LauncherProfile(
      id: id ?? this.id,
      name: name ?? this.name,
      enabledMods: enabledMods ?? this.enabledMods,
      selectedVersions: selectedVersions ?? this.selectedVersions,
      configMetadata: configMetadata,
      launchSettings: launchSettings ?? this.launchSettings,
      worldSelection: worldSelection ?? this.worldSelection,
      backupMetadata: backupMetadata,
    );
  }
}
