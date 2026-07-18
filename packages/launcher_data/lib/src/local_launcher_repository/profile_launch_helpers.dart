part of '../local_launcher_repository.dart';

extension _ProfileLaunchHelpers on LocalLauncherRepository {
  Future<LaunchResult> _startGameWithWorldSelection(
    GameInstall install,
    LauncherProfile profile, {
    required String message,
  }) async {
    final snapshot = await _captureWorldSelection(install);
    late final LaunchResult result;
    try {
      await _writeWorldSelection(install, profile.worldSelection);
      result = await _startGame(install, profile, message: message);
    } on Object {
      await _restoreWorldSelection(snapshot);
      rethrow;
    }
    if (!result.started) {
      await _restoreWorldSelection(snapshot);
    }
    return result;
  }

  Future<_WorldSelectionSnapshot> _captureWorldSelection(
    GameInstall install,
  ) async {
    final file = File(
      p.join(_managerConfig(install).path, 'topiaforge.worlds.json'),
    );
    final type = await FileSystemEntity.type(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return _WorldSelectionSnapshot(file, null);
    }
    if (type != FileSystemEntityType.file) {
      throw StateError('World selection config must be a regular file.');
    }
    return _WorldSelectionSnapshot(
      file,
      await _readLauncherFileBounded(file, _maxWorldConfigBytes),
    );
  }

  Future<void> _restoreWorldSelection(_WorldSelectionSnapshot snapshot) async {
    if (snapshot.contents == null) {
      if (await snapshot.file.exists()) {
        await snapshot.file.delete();
      }
      return;
    }
    await _writeFileBytesAtomic(snapshot.file, snapshot.contents!);
  }

  Future<File> _writeProfileLaunchConfiguration(
    GameInstall install,
    ProfileLaunchConfiguration configuration,
  ) async {
    final staging = _managerStaging(install)..createSync(recursive: true);
    final token = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final file = File(p.join(staging.path, 'launch-profile-$token.json'));
    await _writeJsonFileAtomic(file, configuration.toJson());
    return file;
  }

  Future<String?> _profileSelectionError(
    GameInstall install,
    ProfileLaunchConfiguration configuration,
  ) async {
    if (configuration.safeMode) {
      return null;
    }

    final state = await _readManagerState(install);
    final stateById = _stateByModId(state);
    final catalog = await _loadInstalledVersionCatalog(
      install,
      stateById: stateById,
    );
    final missing = <String>[];
    for (final entry in configuration.selectedVersions.entries) {
      final versions = catalog[entry.key.toLowerCase()];
      if (versions == null ||
          !versions.any((version) => version.version == entry.value)) {
        missing.add('${entry.key} ${entry.value}');
      }
    }

    final effectiveIds = <String>{};
    if (configuration.inheritManagerModState) {
      for (final entry in stateById.entries) {
        if (entry.value['enabled'] as bool? ?? true) {
          effectiveIds.add(entry.key);
        }
      }
      for (final id in configuration.selectedVersions.keys) {
        if (!stateById.containsKey(id.toLowerCase())) {
          effectiveIds.add(id.toLowerCase());
        }
      }
    } else {
      effectiveIds.addAll(
        configuration.enabledMods.map((id) => id.toLowerCase()),
      );
    }

    final selectedMods = <InstalledMod>[];
    for (final key in effectiveIds.toList()..sort()) {
      final versions = catalog[key];
      if (versions == null || versions.isEmpty) {
        missing.add(key);
        continue;
      }
      final selectedVersion = _selectedProfileVersion(
        key,
        configuration,
        stateById[key],
      );
      InstalledMod? selected;
      if (selectedVersion != null) {
        for (final version in versions) {
          if (version.version == selectedVersion) {
            selected = version;
            break;
          }
        }
      } else {
        selected = _pickCurrentVersion(versions, stateById[key]);
      }
      if (selected == null) {
        missing.add('$key $selectedVersion');
        continue;
      }
      selectedMods.add(_profileEnabledMod(selected));
    }

    if (missing.isNotEmpty) {
      final normalizedMissing = missing.toSet().toList()
        ..sort(
          (left, right) => left.toLowerCase().compareTo(right.toLowerCase()),
        );
      return 'Profile ${configuration.profileId} references unavailable '
          'packages: ${normalizedMissing.join(', ')}.';
    }

    final resolution = _dependencyPlanner.resolveInstalled(
      selectedMods,
      gameVersion: install.gameVersion,
      requireKnownGameVersion: true,
      loaderVersion: _loaderVersion,
      sdkVersion: _sdkVersion,
      platform: _gamePlatform(install),
      architecture: _gameArchitecture(install),
      contentTargets: _gameContentTargets(install),
    );
    final blocking = resolution.issues
        .where((issue) => issue.isBlocking)
        .map((issue) => issue.message)
        .toSet()
        .toList();
    if (blocking.isEmpty) {
      return null;
    }
    blocking.sort();
    return 'Profile ${configuration.profileId} cannot launch: '
        '${blocking.join(' ')}';
  }

  String? _selectedProfileVersion(
    String key,
    ProfileLaunchConfiguration configuration,
    Map<dynamic, dynamic>? stateItem,
  ) {
    for (final entry in configuration.selectedVersions.entries) {
      if (entry.key.toLowerCase() == key) {
        return entry.value;
      }
    }
    return stateItem?['version'] as String?;
  }

  InstalledMod _profileEnabledMod(InstalledMod mod) => InstalledMod(
    id: mod.id,
    name: mod.name,
    version: mod.version,
    enabled: true,
    restartRequired: mod.restartRequired,
    uninstallPending: mod.uninstallPending,
    packagePath: mod.packagePath,
    manifest: mod.manifest,
    installedAtUtc: mod.installedAtUtc,
    updatedAtUtc: mod.updatedAtUtc,
    errors: mod.errors,
    versionPinned: mod.versionPinned,
    requestedVersion: mod.requestedVersion,
    selectionReason: mod.selectionReason,
    installedVersions: mod.installedVersions,
    sourceSha256: mod.sourceSha256,
    trust: mod.trust,
    repairable: mod.repairable,
  );

  Map<String, String> _profileLaunchEnvironment(
    GameLayout layout,
    LauncherProfile profile,
    String configurationPath,
  ) {
    final required = layout.launchEnvironment();
    final reserved = {
      ProfileLaunchConfiguration.environmentVariable.toLowerCase(),
      ...required.keys.map((key) => key.toLowerCase()),
    };
    final environment = <String, String>{};
    for (final entry in profile.launchSettings.environment.entries) {
      final key = entry.key;
      if (key.isEmpty ||
          key.contains('=') ||
          key.contains('\u0000') ||
          entry.value.contains('\u0000')) {
        throw FormatException('Profile contains an invalid environment entry.');
      }
      if (reserved.contains(key.toLowerCase())) {
        throw FormatException(
          'Profile environment cannot replace required variable $key.',
        );
      }
      environment[key] = entry.value;
    }
    environment.addAll(required);
    environment[ProfileLaunchConfiguration.environmentVariable] =
        configurationPath;
    return environment;
  }

  Future<void> _deleteProfileLaunchConfiguration(File file) async {
    try {
      if (await file.exists()) {
        await file.delete();
      }
    } on FileSystemException catch (error) {
      await _appendLauncherLogBestEffort(
        'Could not remove unused profile launch configuration: $error',
      );
    }
  }
}

class _WorldSelectionSnapshot {
  const _WorldSelectionSnapshot(this.file, this.contents);

  final File file;
  final Uint8List? contents;
}

Future<Uint8List> _readLauncherFileBounded(File file, int maxBytes) async {
  if (maxBytes < 0) {
    throw ArgumentError.value(maxBytes, 'maxBytes', 'must not be negative');
  }
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('${p.basename(file.path)} cannot be a symbolic link.');
  }
  if (type != FileSystemEntityType.file) {
    throw StateError('${p.basename(file.path)} is not a regular file.');
  }
  if (await file.length() > maxBytes) {
    throw StateError('${p.basename(file.path)} exceeds $maxBytes bytes.');
  }
  final bytes = BytesBuilder(copy: false);
  var length = 0;
  await for (final chunk in file.openRead()) {
    length += chunk.length;
    if (length > maxBytes) {
      throw StateError('${p.basename(file.path)} exceeds $maxBytes bytes.');
    }
    bytes.add(chunk);
  }
  return bytes.takeBytes();
}

const _maxWorldConfigBytes = 1024 * 1024;
