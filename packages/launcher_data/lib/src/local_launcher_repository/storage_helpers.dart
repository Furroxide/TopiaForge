part of '../local_launcher_repository.dart';

const _profileFormatVersion = 2;
const _profileExportSuffix = '.topiaforgeprofile.json';
const _packageSourceFormatVersion = 2;

void _requireProfileExportPath(String path) {
  if (!path.toLowerCase().endsWith(_profileExportSuffix)) {
    throw const FormatException(
      'TopiaForge profile files must end with .topiaforgeprofile.json.',
    );
  }
}

LauncherProfile _requireValidLauncherProfile(LauncherProfile profile) {
  ProfileLaunchConfiguration.fromProfile(profile);
  return profile;
}

extension _StorageHelpers on LocalLauncherRepository {
  Future<List<LauncherProfile>> _loadProfiles() async {
    await _recoverAtomicBackupIfMissing(_profilesFile);
    if (!_profilesFile.existsSync()) {
      final defaults = [LauncherProfile.defaultProfile()];
      await saveProfiles(defaults, defaults.first.id);
      return defaults;
    }

    final decoded = await _readJsonFileBounded(
      _profilesFile,
      maxBytes: _maxProfilesBytes,
      label: 'Launcher profiles',
    );
    if (decoded is! Map || decoded['schemaVersion'] != _profileFormatVersion) {
      throw const FormatException(
        'Launcher profiles must use TopiaForge schemaVersion 2.',
      );
    }
    final profiles = decoded['profiles'] as List?;
    final result = profiles == null
        ? <LauncherProfile>[]
        : profiles
              .whereType<Map>()
              .map(
                (item) => LauncherProfile.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              .map(_requireValidLauncherProfile)
              .toList();
    return result.isEmpty ? [LauncherProfile.defaultProfile()] : result;
  }

  Future<List<PackageSource>> _loadPackageSources() async {
    await _recoverAtomicBackupIfMissing(_sourcesFile);
    if (!_sourcesFile.existsSync()) {
      return _defaultPackageSources();
    }

    final decoded = await _readJsonFileBounded(
      _sourcesFile,
      maxBytes: _maxPackageSourcesBytes,
      label: 'Package sources',
    );
    if (decoded is! Map ||
        decoded['formatVersion'] != _packageSourceFormatVersion) {
      throw const FormatException(
        'Package sources must use TopiaForge formatVersion 2.',
      );
    }
    final sources = decoded['sources'] as List?;
    final builtIns = _defaultPackageSources();
    final parsed = sources == null
        ? <PackageSource>[]
        : sources
              .whereType<Map>()
              .map(
                (item) => PackageSource.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              // Built-in sources are app-managed: always reconcile their URL
              // to the canonical default. Only the player's enabled flag is
              // configurable.
              .map((source) {
                final builtIn = builtIns
                    .where((item) => item.id == source.id)
                    .firstOrNull;
                return builtIn == null
                    ? source
                    : builtIn.copyWith(enabled: source.enabled);
              })
              .toList();
    _validatePackageSources(parsed);
    // Required built-ins can be disabled but never removed.
    for (final builtIn in builtIns) {
      if (!parsed.any((source) => source.id == builtIn.id)) {
        parsed.add(builtIn);
      }
    }
    return parsed;
  }

  List<PackageSource> _defaultPackageSources() {
    return [
      PackageSource(
        id: 'io.github.furroxide.topiaforge.local',
        name: 'Bundled Local Packages',
        // Point at the directory of built .topiaforgemod packages. The catalog is derived
        // directly from those packages (manifest + sha read from each file), so there is no
        // separate metadata to drift out of sync with the packages themselves.
        url: Uri.file(p.join(_repositoryRoot.path, 'dist')).toString(),
        builtIn: true,
      ),
      const PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        builtIn: true,
      ),
    ];
  }

  Future<Map<String, Object?>> _loadSettings() async {
    await _recoverAtomicBackupIfMissing(_settingsFile);
    if (!_settingsFile.existsSync()) {
      return <String, Object?>{};
    }

    final decoded = await _readJsonFileBounded(
      _settingsFile,
      maxBytes: _maxSettingsBytes,
      label: 'Launcher settings',
    );
    return decoded is Map<String, Object?> ? decoded : <String, Object?>{};
  }

  Future<void> _updateSettings(
    void Function(Map<String, Object?> settings) update,
  ) async {
    final previous = _settingsMutationTail;
    final completion = Completer<void>();
    _settingsMutationTail = completion.future;
    await previous;
    try {
      final settings = await _loadSettings();
      update(settings);
      await _saveSettings(settings);
    } finally {
      completion.complete();
    }
  }

  Future<void> _saveSettings(Map<String, Object?> settings) async {
    await _writeJsonFileAtomic(
      _settingsFile,
      settings,
      maxBytes: _maxSettingsBytes,
      label: 'Launcher settings',
    );
  }

  Future<String> _readLauncherLog({int maxLines = 200}) async {
    final type = FileSystemEntity.typeSync(
      _launcherLogFile.path,
      followLinks: false,
    );
    if (type == FileSystemEntityType.notFound) {
      return '';
    }
    if (type != FileSystemEntityType.file) {
      throw StateError('Launcher log is not a regular file.');
    }

    return (await _readTailLinesBounded(
      _launcherLogFile,
      maxLines: maxLines,
      maxBytes: _maxLauncherLogReadBytes,
    )).join('\n');
  }

  Future<void> _appendLauncherLog(String message) async {
    final previous = _launcherLogMutationTail;
    final completion = Completer<void>();
    _launcherLogMutationTail = completion.future;
    await previous;
    try {
      await _launcherLogFile.parent.create(recursive: true);
      final type = FileSystemEntity.typeSync(
        _launcherLogFile.path,
        followLinks: false,
      );
      if (type == FileSystemEntityType.link) {
        throw StateError('Launcher log cannot be a symbolic link.');
      }
      if (type != FileSystemEntityType.notFound &&
          type != FileSystemEntityType.file) {
        throw StateError('Launcher log is not a regular file.');
      }
      if (type == FileSystemEntityType.file &&
          await _launcherLogFile.length() > _maxLauncherLogBytes) {
        final tail = await _readTailLinesBounded(
          _launcherLogFile,
          maxLines: 100000,
          maxBytes: _maxLauncherLogReadBytes,
        );
        await _writeFileBytesAtomic(
          _launcherLogFile,
          utf8.encode(tail.isEmpty ? '' : '${tail.join('\n')}\n'),
        );
      }
      await _launcherLogFile.writeAsString(
        '${DateTime.now().toUtc().toIso8601String()} '
        '${_sanitizeLauncherLogMessage(message)}\n',
        mode: FileMode.append,
        flush: true,
      );
    } finally {
      completion.complete();
    }
  }

  Future<WorldCatalog> _loadWorldCatalog(
    GameInstall install,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) async {
    final file = File(
      p.join(
        _managerData(install).path,
        WorldCatalog.worldsModId,
        'catalog.json',
      ),
    );
    WorldCatalog catalog;
    if (!file.existsSync()) {
      catalog = WorldCatalog.fallback();
    } else {
      try {
        final decoded = await _readJsonFileBounded(
          file,
          maxBytes: _maxWorldCatalogBytes,
          label: 'World catalog',
        );
        catalog = WorldCatalog.fromJson(decoded as Map<String, Object?>);
      } on Object catch (error) {
        await _appendLauncherLogBestEffort('World catalog read failed: $error');
        catalog = WorldCatalog.fallback();
      }
    }

    return _mergeManifestGamemodes(catalog, installedMods, registryMods);
  }

  WorldCatalog _mergeManifestGamemodes(
    WorldCatalog catalog,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) {
    final gamemodes = [...catalog.gamemodes];
    final seen = {for (final gamemode in gamemodes) gamemode.id.toLowerCase()};
    final installedIds = {
      for (final mod in installedMods.where((mod) => mod.enabled))
        mod.id.toLowerCase(),
    };

    for (final mod in installedMods.where((mod) => mod.enabled)) {
      for (final gamemode in mod.manifest?.worldGamemodes ?? const []) {
        if (ModManifest.isValidId(gamemode.id) &&
            gamemode.name.trim().isNotEmpty &&
            seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    for (final mod in registryMods.where(
      (mod) => installedIds.contains(mod.manifest.id.toLowerCase()),
    )) {
      for (final gamemode in mod.manifest.worldGamemodes) {
        if (ModManifest.isValidId(gamemode.id) &&
            gamemode.name.trim().isNotEmpty &&
            seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    return WorldCatalog(
      worlds: catalog.worlds,
      gamemodes: gamemodes,
      menuEntries: catalog.menuEntries,
    );
  }
}

Future<void> _recoverAtomicBackupIfMissing(File file) async {
  if (await file.exists() || !await file.parent.exists()) {
    return;
  }
  final prefix = '${p.basename(file.path)}.';
  final candidates =
      file.parent.listSync(followLinks: false).whereType<File>().where((
        candidate,
      ) {
        final name = p.basename(candidate.path);
        return name.startsWith(prefix) && name.endsWith('.bak');
      }).toList()..sort(
        (left, right) =>
            right.statSync().modified.compareTo(left.statSync().modified),
      );
  for (final backup in candidates) {
    if (FileSystemEntity.typeSync(backup.path, followLinks: false) !=
        FileSystemEntityType.file) {
      continue;
    }
    try {
      await backup.rename(file.path);
      for (final stale in candidates.where(
        (candidate) => candidate != backup,
      )) {
        await _deleteFileBestEffort(stale);
      }
      return;
    } on FileSystemException {
      // Try the next intact backup if this one vanished or was locked.
    }
  }
}

Future<Object?> _readJsonFileBounded(
  File file, {
  required int maxBytes,
  required String label,
}) async {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('$label cannot be read through a symbolic link.');
  }
  if (type != FileSystemEntityType.file) {
    throw StateError('$label does not exist: ${file.path}');
  }
  try {
    return jsonDecode(
      utf8.decode(await _readLauncherFileBounded(file, maxBytes)),
    );
  } on StateError {
    rethrow;
  } on Object catch (error) {
    throw FormatException('$label is not valid JSON: $error');
  }
}

Future<List<String>> _readTailLinesBounded(
  File file, {
  required int maxLines,
  required int maxBytes,
}) async {
  if (maxLines <= 0) {
    return const [];
  }
  final length = await file.length();
  final start = length > maxBytes ? length - maxBytes : 0;
  final bytes = BytesBuilder(copy: false);
  await for (final chunk in file.openRead(start, length)) {
    if (chunk.length > maxBytes - bytes.length) {
      throw StateError('Log tail exceeded its $maxBytes-byte limit.');
    }
    bytes.add(chunk);
  }
  var text = utf8.decode(bytes.takeBytes(), allowMalformed: true);
  if (start > 0) {
    final firstNewline = text.indexOf('\n');
    text = firstNewline < 0 ? '' : text.substring(firstNewline + 1);
  }
  return _tailStatic(const LineSplitter().convert(text), maxLines);
}

List<String> _tailStatic(List<String> lines, int maxLines) {
  if (lines.length <= maxLines) {
    return lines;
  }
  return lines.sublist(lines.length - maxLines);
}

const _maxSettingsBytes = 1024 * 1024;
const _maxProfilesBytes = 4 * 1024 * 1024;
const _maxPackageSourcesBytes = 1024 * 1024;
const _maxWorldCatalogBytes = 16 * 1024 * 1024;
const _maxLauncherLogReadBytes = 4 * 1024 * 1024;
const _maxLauncherLogBytes = 8 * 1024 * 1024;
const _maxLauncherLogMessageCharacters = 4096;

String _sanitizeLauncherLogMessage(String message) {
  final singleLine = message.replaceAll(RegExp(r'[\u0000-\u001f\u007f]+'), ' ');
  return singleLine.length <= _maxLauncherLogMessageCharacters
      ? singleLine
      : '${singleLine.substring(0, _maxLauncherLogMessageCharacters)}…';
}

Future<void> _writeJsonFileAtomic(
  File file,
  Map<String, Object?> payload, {
  int? maxBytes,
  String label = 'JSON file',
}) async {
  final json = const JsonEncoder.withIndent('  ').convert(payload);
  final bytes = utf8.encode(json);
  if (maxBytes != null && bytes.length > maxBytes) {
    throw StateError('$label exceeds its $maxBytes-byte limit.');
  }
  await _writeFileBytesAtomic(file, bytes);
}

Future<void> _writeFileBytesAtomic(File file, List<int> contents) async {
  await file.parent.create(recursive: true);
  final targetType = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (targetType == FileSystemEntityType.link) {
    throw StateError('Refusing to replace symbolic link: ${file.path}');
  }
  if (targetType != FileSystemEntityType.notFound &&
      targetType != FileSystemEntityType.file) {
    throw StateError('Expected a regular file: ${file.path}');
  }
  final token = '$pid-${DateTime.now().microsecondsSinceEpoch}';
  final temp = File('${file.path}.$token.tmp');
  final backup = File('${file.path}.$token.bak');
  var committed = false;
  try {
    await temp.writeAsBytes(contents, flush: true);
    try {
      await temp.rename(file.path);
      committed = true;
      return;
    } on FileSystemException {
      if (!await file.exists()) {
        rethrow;
      }
    }

    await file.rename(backup.path);
    try {
      await temp.rename(file.path);
      committed = true;
    } on Object {
      if (!await file.exists() && await backup.exists()) {
        await backup.rename(file.path);
      }
      rethrow;
    }
  } finally {
    // Once the live file has been replaced, cleanup must not make callers
    // treat the committed write as a failure. In particular, Windows uses the
    // backup-swap branch and antivirus/file indexing can briefly retain the
    // old file.
    await _deleteFileBestEffort(temp);
    if (committed) {
      await _deleteFileBestEffort(backup);
    }
  }
}

Future<void> _deleteFileBestEffort(File file) async {
  try {
    if (await file.exists()) {
      await file.delete();
    }
  } on Object {
    // A stale temp/backup is recoverable and must not invalidate a committed
    // live file.
  }
}
