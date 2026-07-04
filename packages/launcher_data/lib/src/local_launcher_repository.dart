import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

part 'local_launcher_repository/game_layout.dart';
part 'local_launcher_repository/game_runtime_helpers.dart';
part 'local_launcher_repository/legacy_diagnostics_helpers.dart';
part 'local_launcher_repository/manager_state_helpers.dart';
part 'local_launcher_repository/package_installation_helpers.dart';
part 'local_launcher_repository/package_helpers.dart';
part 'local_launcher_repository/path_helpers.dart';
part 'local_launcher_repository/process_helpers.dart';
part 'local_launcher_repository/runtime_repair_helpers.dart';
part 'local_launcher_repository/storage_helpers.dart';

class LocalLauncherRepository implements LauncherRepository {
  LocalLauncherRepository({
    String? dataRoot,
    String? repositoryRoot,
    String? knownGamePath,
    DependencyPlanner dependencyPlanner = const DependencyPlanner(),
  }) : _dataRoot = Directory(dataRoot ?? _defaultDataRoot()),
       _repositoryRoot = Directory(repositoryRoot ?? _findRepositoryRoot()),
       _knownGamePath = knownGamePath,
       _dependencyPlanner = dependencyPlanner;

  final Directory _dataRoot;
  final Directory _repositoryRoot;
  final String? _knownGamePath;
  final DependencyPlanner _dependencyPlanner;

  static const _bepInExVersion = '5.4.23.5';
  static const _loaderVersion = RobotopiaRuntimeVersions.loaderVersion;
  static const _sdkVersion = RobotopiaRuntimeVersions.sdkVersion;
  @override
  String get dataRoot => _dataRoot.path;

  File get _settingsFile => File(p.join(_dataRoot.path, 'settings.json'));
  File get _profilesFile => File(p.join(_dataRoot.path, 'profiles.json'));
  File get _sourcesFile => File(p.join(_dataRoot.path, 'package_sources.json'));
  File get _launcherLogFile =>
      File(p.join(_dataRoot.path, 'logs', 'launcher.log'));
  Directory get _packageCache =>
      Directory(p.join(_dataRoot.path, 'package-cache'));

  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    _ensureDataRoot();
    final profiles = await _loadProfiles();
    final settings = await _loadSettings();
    final selectedProfileId =
        (settings['selectedProfileId'] as String?) ?? profiles.first.id;
    final configuredPath = settings['gamePath'] as String?;
    final gameInstall =
        configuredPath != null && configuredPath.trim().isNotEmpty
        ? await _validateGameDirectory(configuredPath)
        : await detectKnownInstall();
    final installedMods = gameInstall == null
        ? <InstalledMod>[]
        : await _loadInstalledMods(gameInstall);
    final packageSources = await _loadPackageSources();
    final registryMods = await _loadRegistryMods(installedMods, packageSources);
    return LauncherSnapshot(
      gameInstall: gameInstall,
      profiles: profiles,
      selectedProfileId: selectedProfileId,
      installedMods: installedMods,
      registryMods: registryMods,
      packageSources: packageSources,
      worldCatalog: gameInstall == null
          ? WorldCatalog.fallback()
          : await _loadWorldCatalog(gameInstall, installedMods, registryMods),
      legacyMods: gameInstall == null
          ? <LegacyMod>[]
          : await detectLegacyMods(gameInstall),
      recentLog: gameInstall == null
          ? await _readLauncherLog()
          : await readRecentLog(gameInstall),
      launcherUpdates: LauncherUpdateSettings.fromJson(
        _objectMap(settings['launcherUpdates']),
      ),
      developerMode: (settings['developerMode'] as bool?) ?? false,
    );
  }

  @override
  Future<void> setDeveloperMode(bool enabled) async {
    final settings = await _loadSettings();
    settings['developerMode'] = enabled;
    await _saveSettings(settings);
  }

  @override
  Future<void> saveLauncherUpdateSettings(
    LauncherUpdateSettings settings,
  ) async {
    final persisted = await _loadSettings();
    persisted['launcherUpdates'] = settings.toJson();
    await _saveSettings(persisted);
  }

  @override
  Future<GameInstall?> detectKnownInstall() async {
    final knownPath = _knownGamePath ?? _defaultKnownGamePath();
    if (knownPath == null || GameLayout.resolve(knownPath) == null) {
      return null;
    }
    return _validateGameDirectory(knownPath);
  }

  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    final install = await _validateGameDirectory(path);
    if (install.issues.any((issue) => issue.isBlocking)) {
      throw StateError(install.issues.map((issue) => issue.message).join(' '));
    }
    final settings = await _loadSettings();
    settings['gamePath'] = install.path;
    await _saveSettings(settings);
    await _appendLauncherLog('Selected game directory ${install.path}.');
    return install;
  }

  @override
  Future<GameCompatStatus> checkGameCompat(GameInstall install) async {
    final layout = GameLayout.resolve(install.path);
    if (layout == null) {
      return GameCompatStatus.skipped();
    }
    return _checkGameCompat(
      Directory(layout.gameRoot),
      Directory(layout.managedDirPath),
      force: true,
    );
  }

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) =>
      _installOrRepairRuntime(install);

  @override
  Future<PackageInstallPlan> previewPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) async {
    return _previewPackageInstallPlan(
      packagePath,
      install,
      expectedSha256: expectedSha256,
      sourceId: sourceId,
      sourceName: sourceName,
    );
  }

  @override
  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
  }) => _installPackage(packagePath, install, expectedSha256: expectedSha256);

  @override
  Future<List<PackageSource>> savePackageSources(
    List<PackageSource> sources,
  ) async {
    final normalized = sources.isEmpty ? _defaultPackageSources() : sources;
    await _sourcesFile.create(recursive: true);
    await _sourcesFile.writeAsString(
      _prettyJson({
        'sources': normalized.map((source) => source.toJson()).toList(),
      }),
    );
    await _appendLauncherLog('Saved ${normalized.length} package sources.');
    return normalized;
  }

  void _extractPackageToInstall(
    _PackageReadResult package,
    GameInstall install,
  ) {
    final target = Directory(
      p.join(
        _packagesRoot(install).path,
        package.manifest.id,
        package.manifest.version,
      ),
    );
    if (target.existsSync()) {
      target.deleteSync(recursive: true);
    }
    target.createSync(recursive: true);

    for (final file in package.archive.files) {
      final outputPath = p.join(target.path, _safeArchivePath(file.name));
      if (file.isFile) {
        File(outputPath)
          ..createSync(recursive: true)
          ..writeAsBytesSync(file.content as List<int>);
      } else {
        Directory(outputPath).createSync(recursive: true);
      }
    }
  }

  @override
  Future<List<InstalledMod>> installInboxPackages(GameInstall install) async {
    final inbox = _packageInbox(install);
    if (!inbox.existsSync()) {
      return _loadInstalledMods(install);
    }

    for (final file in inbox.listSync().whereType<File>().where(
      (file) => file.path.toLowerCase().endsWith('.robotopiamod'),
    )) {
      try {
        await installPackage(file.path, install);
      } on Object catch (error) {
        await _appendLauncherLog(
          'Inbox install failed for ${file.path}: $error',
        );
      }
    }

    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  ) async {
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      if ((item['id'] as String?)?.toLowerCase() == modId.toLowerCase()) {
        item['enabled'] = enabled;
        item['restartRequired'] = true;
        item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
      }
    }
    await _saveManagerState(install, state);
    await _appendLauncherLog('${enabled ? 'Enabled' : 'Disabled'} $modId.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> disableAllMods(GameInstall install) async {
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      item['enabled'] = false;
      item['restartRequired'] = true;
      item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
    }
    await _saveManagerState(install, state);
    await _appendLauncherLog('Disabled all mods.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> uninstallMod(
    GameInstall install,
    String modId,
  ) async {
    final modRoot = Directory(p.join(_packagesRoot(install).path, modId));
    if (modRoot.existsSync()) {
      modRoot.deleteSync(recursive: true);
    }

    final state = await _readManagerState(install);
    final mods = (state['mods'] as List).whereType<Map>().toList();
    mods.removeWhere(
      (item) => (item['id'] as String?)?.toLowerCase() == modId.toLowerCase(),
    );
    state['mods'] = mods;
    await _saveManagerState(install, state);
    await _appendLauncherLog('Uninstalled $modId.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    final normalizedProfiles = profiles.isEmpty
        ? [LauncherProfile.defaultProfile()]
        : profiles;
    await _profilesFile
        .create(recursive: true)
        .then(
          (file) => file.writeAsString(
            _prettyJson({
              'profiles': normalizedProfiles
                  .map((profile) => profile.toJson())
                  .toList(),
            }),
          ),
        );

    final settings = await _loadSettings();
    settings['selectedProfileId'] = selectedProfileId;
    await _saveSettings(settings);
    return normalizedProfiles;
  }

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    await _writeWorldSelection(install, profile.worldSelection);
    final message = profile.launchSettings.safeMode
        ? 'Launched Robotopia in safe mode. All mods were disabled first.'
        : 'Launched Robotopia.';
    return _startGame(install, profile, message: message);
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    await _writeWorldSelection(install, profile.worldSelection);
    final stopped = await _stopGameIfRunning(install);
    final message = switch ((stopped, profile.launchSettings.safeMode)) {
      (true, true) =>
        'Restarted Robotopia in safe mode. All mods were disabled first.',
      (true, false) => 'Restarted Robotopia.',
      (false, true) =>
        'Started Robotopia in safe mode. No running process was found.',
      (false, false) => 'Started Robotopia. No running process was found.',
    };
    return _startGame(install, profile, message: message);
  }

  @override
  Future<List<LegacyMod>> detectLegacyMods(GameInstall install) =>
      _detectLegacyMods(install);

  @override
  Future<String> deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) async {
    final path = p.join(
      _managerConfig(install).path,
      'robotopia.ugc.livesync.json',
    );
    final file = File(path);
    await file.create(recursive: true);
    await file.writeAsString(_prettyJson(settings.toRuntimeConfig()));
    return file.path;
  }

  @override
  Future<UgcLiveSyncStatusSnapshot?> readUgcLiveSyncStatus(
    GameInstall install,
  ) async {
    final path = p.join(
      _managerConfig(install).path,
      'robotopia.ugc.livesync.status.json',
    );
    final file = File(path);
    if (!file.existsSync()) {
      return null;
    }
    try {
      final decoded = jsonDecode(await file.readAsString());
      if (decoded is Map<String, Object?>) {
        return UgcLiveSyncStatusSnapshot.fromJson(decoded);
      }
    } on Object {
      /* Ignore malformed or half-written status files. */
    }
    return null;
  }

  @override
  Future<List<UgcSceneRef>> listWatchFolderScenes(String watchFolder) async {
    if (watchFolder.trim().isEmpty) {
      return const [];
    }
    final dir = Directory(watchFolder);
    if (!dir.existsSync()) {
      return const [];
    }

    File? newest;
    DateTime newestTime = DateTime.fromMillisecondsSinceEpoch(0);
    for (final entity in dir.listSync().whereType<File>()) {
      final lower = entity.path.toLowerCase();
      if (!lower.endsWith('.json') && !lower.endsWith('.json.gz')) {
        continue;
      }
      final modified = entity.statSync().modified;
      if (newest == null || modified.isAfter(newestTime)) {
        newest = entity;
        newestTime = modified;
      }
    }
    if (newest == null) {
      return const [];
    }

    try {
      var bytes = await newest.readAsBytes();
      if (newest.path.toLowerCase().endsWith('.gz') ||
          (bytes.length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)) {
        bytes = GZipDecoder().decodeBytes(bytes);
      }
      var text = utf8.decode(bytes, allowMalformed: true);
      if (text.isNotEmpty && text.codeUnitAt(0) == 0xfeff) {
        text = text.substring(1);
      }
      final decoded = jsonDecode(text);
      if (decoded is! Map<String, Object?>) {
        return const [];
      }
      final scenes = decoded['scenes'];
      if (scenes is! Map) {
        return const [];
      }
      final result = <UgcSceneRef>[];
      scenes.forEach((key, value) {
        final id = value is Map && value['id'] is String
            ? value['id'] as String
            : key.toString();
        final name = value is Map && value['name'] is String
            ? value['name'] as String
            : '';
        result.add(UgcSceneRef(id: id, name: name));
      });
      return result;
    } on Object {
      return const [];
    }
  }

  @override
  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  ) => _createDiagnosticBundle(install, resolution);

  @override
  Future<String> readRecentLog(GameInstall install, {int maxLines = 200}) =>
      _readRecentCombinedLog(install, maxLines: maxLines);

  @override
  Future<void> openPath(String path) => _openPath(path);
}
