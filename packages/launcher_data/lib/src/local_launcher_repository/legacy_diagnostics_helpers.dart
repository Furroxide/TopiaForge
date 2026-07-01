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
        mods.add(_legacyDirectoryMod(entity));
      }
    }

    mods.sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return mods;
  }

  LegacyMod _legacyDirectoryMod(Directory directory) {
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
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>,
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
    _addDiagnosticSummary(archive, included, install, resolution, now);
    await _addFileIfExists(
      archive,
      included,
      'launcher.log',
      _launcherLogFile,
      install.path,
    );
    await _addFileIfExists(
      archive,
      included,
      'manager-state.json',
      _managerStateFile(install),
      install.path,
    );
    await _addFileIfExists(
      archive,
      included,
      'manager.log',
      File(p.join(_managerLogs(install).path, 'manager.log')),
      install.path,
    );
    await _addFileIfExists(
      archive,
      included,
      'bepinex-log.txt',
      File(p.join(install.path, 'BepInEx', 'LogOutput.log')),
      install.path,
    );

    await bundle.writeAsBytes(ZipEncoder().encode(archive));
    await _appendLauncherLog('Created diagnostic bundle ${bundle.path}.');
    return DiagnosticBundle(
      path: bundle.path,
      createdAtUtc: now,
      includedFiles: included,
    );
  }

  void _addDiagnosticSummary(
    Archive archive,
    List<String> included,
    GameInstall install,
    DependencyResolutionResult resolution,
    DateTime now,
  ) {
    void addText(String name, String content) {
      archive.addFile(ArchiveFile.string(name, _redact(content, install.path)));
      included.add(name);
    }

    addText(
      'summary.json',
      _prettyJson({
        'createdAtUtc': now.toIso8601String(),
        'gamePath': _redact(install.path, install.path),
        'bepInExStatus': install.bepInExStatus.name,
        'loaderStatus': install.loaderStatus.name,
        'issues': install.issues.map((issue) => issue.toJson()).toList(),
      }),
    );
    addText(
      'load-order.json',
      _prettyJson({
        'orderedMods': resolution.orderedMods.map((mod) => mod.id).toList(),
        'dependencyGraph': resolution.graph,
        'issues': resolution.issues.map((issue) => issue.toJson()).toList(),
      }),
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
    if (managerLog.existsSync()) {
      allLines.add('[manager]');
      allLines.addAll(_tail(managerLog.readAsLinesSync(), maxLines));
    }

    return allLines.join('\n');
  }
}
