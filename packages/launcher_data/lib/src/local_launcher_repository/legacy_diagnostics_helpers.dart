part of '../local_launcher_repository.dart';

extension _LegacyDiagnosticsHelpers on LocalLauncherRepository {
  Future<List<LegacyMod>> _detectLegacyMods(GameInstall install) async {
    final legacyRoot = Directory(p.join(install.path, 'Mods'));
    if (!legacyRoot.existsSync()) {
      return const [];
    }

    final mods = <LegacyMod>[];
    for (final entity in legacyRoot.listSync()) {
      if (entity is File && entity.path.toLowerCase().endsWith('.dll')) {
        final id = p.basenameWithoutExtension(entity.path);
        mods.add(
          LegacyMod(
            id: id,
            name: id,
            path: entity.path,
            kind: 'RoboPatch DLL',
            details: 'Drop-in legacy DLL detected.',
          ),
        );
      } else if (entity is Directory) {
        mods.add(await _legacyDirectoryMod(entity));
      }
    }

    mods.sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return mods;
  }

  Future<LegacyMod> _legacyDirectoryMod(Directory directory) async {
    final manifestFile = File(p.join(directory.path, 'robotopia.mod.json'));
    if (!manifestFile.existsSync()) {
      return LegacyMod(
        id: p.basename(directory.path),
        name: p.basename(directory.path),
        path: directory.path,
        kind: 'RoboPatch folder',
        details: 'No Robotopia manifest found.',
      );
    }

    try {
      final manifest = ModManifest.fromJson(
        jsonDecode(
              utf8.decode(
                await _readLauncherFileBounded(
                  manifestFile,
                  _maxLauncherManifestBytes,
                ),
              ),
            )
            as Map<String, Object?>,
      );
      return LegacyMod(
        id: manifest.id,
        name: manifest.name,
        path: directory.path,
        kind: 'RoboPatch folder',
        canMigrate: true,
        details: 'Manifest found; can be repacked as .robotopiamod.',
      );
    } on Object catch (error) {
      return LegacyMod(
        id: p.basename(directory.path),
        name: p.basename(directory.path),
        path: directory.path,
        kind: 'RoboPatch folder',
        details: 'Manifest exists but could not be read: $error',
      );
    }
  }

  Future<DiagnosticBundle> _createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  ) async {
    final now = DateTime.now().toUtc();
    final diagnosticsDir = Directory(p.join(_dataRoot.path, 'diagnostics'))
      ..createSync(recursive: true);
    final bundle = File(
      p.join(
        diagnosticsDir.path,
        'robotopia-diagnostics-${now.toIso8601String().replaceAll(':', '-')}.zip',
      ),
    );

    final archive = Archive();
    final included = <String>[];
    final entries = <DiagnosticEntryMetadata>[];
    _addDiagnosticSummary(archive, included, entries, install, resolution, now);
    await _addDiagnosticFileIfExists(
      archive,
      included,
      entries,
      'launcher.log',
      _launcherLogFile,
      install.path,
    );
    await _addDiagnosticFileIfExists(
      archive,
      included,
      entries,
      'manager-state.json',
      _managerStateFile(install),
      install.path,
    );
    await _addDiagnosticFileIfExists(
      archive,
      included,
      entries,
      'manager.log',
      File(p.join(_managerLogs(install).path, 'manager.log')),
      install.path,
    );
    await _addDiagnosticFileIfExists(
      archive,
      included,
      entries,
      'bepinex-log.txt',
      File(p.join(install.path, 'BepInEx', 'LogOutput.log')),
      install.path,
    );
    final manifest = _prettyJson({
      'schemaVersion': 1,
      'hashAlgorithm': 'SHA-256',
      'selfHashExcluded': true,
      'entries': entries.map((entry) => entry.toJson()).toList(),
    });
    entries.add(
      _addDiagnosticText(
        archive,
        included,
        'diagnostic-manifest.json',
        manifest,
        install.path,
      ),
    );

    final bytes = ZipEncoder().encode(archive);
    if (bytes.length > _maxDiagnosticBundleBytes) {
      throw StateError('Diagnostic bundle exceeds the 16 MB output limit.');
    }
    await _writeFileBytesAtomic(bundle, bytes);
    await _appendLauncherLog('Created diagnostic bundle ${bundle.path}.');
    return DiagnosticBundle(
      path: bundle.path,
      createdAtUtc: now,
      includedFiles: included,
      entries: entries,
    );
  }

  void _addDiagnosticSummary(
    Archive archive,
    List<String> included,
    List<DiagnosticEntryMetadata> entries,
    GameInstall install,
    DependencyResolutionResult resolution,
    DateTime now,
  ) {
    entries.add(
      _addDiagnosticText(
        archive,
        included,
        'summary.json',
        _prettyJson({
          'createdAtUtc': now.toIso8601String(),
          'gamePath': _redact(install.path, install.path),
          'bepInExStatus': install.bepInExStatus.name,
          'loaderStatus': install.loaderStatus.name,
          if (install.gameVersion != null) 'gameVersion': install.gameVersion,
          'gameVersionLabel': install.gameVersionLabel,
          'issues': install.issues.map((issue) => issue.toJson()).toList(),
        }),
        install.path,
      ),
    );
    entries.add(
      _addDiagnosticText(
        archive,
        included,
        'load-order.json',
        _prettyJson({
          'orderedMods': resolution.orderedMods.map((mod) => mod.id).toList(),
          'dependencyGraph': resolution.graph,
          'issues': resolution.issues.map((issue) => issue.toJson()).toList(),
        }),
        install.path,
      ),
    );
  }

  Future<void> _addDiagnosticFileIfExists(
    Archive archive,
    List<String> included,
    List<DiagnosticEntryMetadata> entries,
    String name,
    File file,
    String gamePath,
  ) async {
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return;
    }
    if (type != FileSystemEntityType.file) {
      return;
    }
    final sourceBytes = await file.length();
    final lines = await _readTailLinesBounded(
      file,
      maxLines: _maxDiagnosticSourceLines + 1,
      maxBytes: _maxDiagnosticSourceBytes,
    );
    final reasons = <String>[];
    if (sourceBytes > _maxDiagnosticSourceBytes) {
      reasons.add('byteLimit');
    }
    if (lines.length > _maxDiagnosticSourceLines) {
      reasons.add('lineLimit');
      lines.removeAt(0);
    }
    final text = [
      if (reasons.isNotEmpty)
        '[earlier content omitted by diagnostic ${reasons.join(' and ')}]',
      ...lines,
    ].join('\n');
    entries.add(
      _addDiagnosticText(
        archive,
        included,
        name,
        text,
        gamePath,
        sourceBytes: sourceBytes,
        truncationReasons: reasons,
        byteLimit: _maxDiagnosticSourceBytes,
        lineLimit: _maxDiagnosticSourceLines,
      ),
    );
  }

  DiagnosticEntryMetadata _addDiagnosticText(
    Archive archive,
    List<String> included,
    String name,
    String text,
    String gamePath, {
    int? sourceBytes,
    List<String> truncationReasons = const [],
    int? byteLimit,
    int? lineLimit,
  }) {
    final raw = utf8.encode(text);
    final bytes = utf8.encode(_redact(text, gamePath));
    archive.addFile(ArchiveFile.bytes(name, bytes));
    included.add(name);
    return DiagnosticEntryMetadata(
      name: name,
      sha256: sha256.convert(bytes).toString(),
      sourceBytes: sourceBytes ?? raw.length,
      includedBytes: bytes.length,
      truncated: truncationReasons.isNotEmpty,
      truncationReasons: truncationReasons,
      byteLimit: byteLimit,
      lineLimit: lineLimit,
    );
  }

  Future<String> _readRecentCombinedLog(
    GameInstall install, {
    required int maxLines,
  }) async {
    final allLines = <String>[];
    final launcherLog = await _readLauncherLog(maxLines: maxLines);
    if (launcherLog.isNotEmpty) {
      allLines.add('[launcher]');
      allLines.addAll(launcherLog.split('\n'));
    }

    final managerLog = File(p.join(_managerLogs(install).path, 'manager.log'));
    if (FileSystemEntity.typeSync(managerLog.path, followLinks: false) ==
        FileSystemEntityType.file) {
      allLines.add('[manager]');
      allLines.addAll(
        await _readTailLinesBounded(
          managerLog,
          maxLines: maxLines.clamp(0, 10000),
          maxBytes: _maxDiagnosticSourceBytes,
        ),
      );
    }

    return _redact(allLines.join('\n'), install.path);
  }
}

const _maxDiagnosticBundleBytes = 16 * 1024 * 1024;
