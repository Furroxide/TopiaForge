part of '../local_launcher_repository.dart';

void _requireSafeModId(String modId) {
  if (!ModManifest.isValidId(modId)) {
    throw ArgumentError.value(
      modId,
      'modId',
      'must use the safe mod id format',
    );
  }
}

extension _PackageInstallationHelpers on LocalLauncherRepository {
  Future<List<PackageSource>> _savePackageSources(
    List<PackageSource> sources,
  ) async {
    final normalized = sources.isEmpty ? _defaultPackageSources() : sources;
    _validatePackageSources(normalized);
    await _writeJsonFileAtomic(
      _sourcesFile,
      {
        'formatVersion': _packageSourceFormatVersion,
        'sources': normalized.map((source) => source.toJson()).toList(),
      },
      maxBytes: _maxPackageSourcesBytes,
      label: 'Package sources',
    );
    await _appendLauncherLogBestEffort(
      'Saved ${normalized.length} package sources.',
    );
    return normalized;
  }

  Future<PackageInstallPlan> _previewPackageInstallPlan(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) async {
    final currentInstall = await _validateGameDirectory(install.path);
    if (!currentInstall.canLaunch) {
      throw StateError(
        currentInstall.issues.map((issue) => issue.message).join(' '),
      );
    }
    final package = await _readPackage(
      packagePath,
      expectedSha256: expectedSha256,
    );
    final installed = await _loadInstalledMods(currentInstall);
    final sources = await _loadPackageSources();
    final registryMods = await _loadRegistryCandidates(installed, sources);
    return _dependencyPlanner.previewInstall(
      package.manifest,
      installed,
      packageSha256: package.sha256Hex,
      packageUrl: package.reference,
      sourceId: sourceId,
      sourceName: sourceName,
      availableMods: registryMods,
      gameVersion: currentInstall.gameVersion,
      requireKnownGameVersion: true,
      loaderVersion: _loaderVersion,
      sdkVersion: _sdkVersion,
      platform: _gamePlatform(currentInstall),
      architecture: _gameArchitecture(currentInstall),
      contentTargets: _gameContentTargets(currentInstall),
    );
  }

  Future<List<InstalledMod>> _installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String rootSourceKind = '',
  }) async {
    final currentInstall = await _validateGameDirectory(install.path);
    if (!currentInstall.canLaunch) {
      throw StateError(
        currentInstall.issues.map((issue) => issue.message).join(' '),
      );
    }
    final package = await _readPackage(
      packagePath,
      expectedSha256: expectedSha256,
    );
    final installed = await _loadInstalledMods(currentInstall);
    final sources = await _loadPackageSources();
    final registryMods = await _loadRegistryCandidates(installed, sources);
    final plan = _dependencyPlanner.previewInstall(
      package.manifest,
      installed,
      packageSha256: package.sha256Hex,
      packageUrl: package.reference,
      sourceId: sourceId,
      availableMods: registryMods,
      gameVersion: currentInstall.gameVersion,
      requireKnownGameVersion: true,
      loaderVersion: _loaderVersion,
      sdkVersion: _sdkVersion,
      platform: _gamePlatform(currentInstall),
      architecture: _gameArchitecture(currentInstall),
      contentTargets: _gameContentTargets(currentInstall),
    );
    final blocking = plan.issues.where((issue) => issue.isBlocking).toList();
    if (blocking.isNotEmpty) {
      throw StateError(blocking.map((issue) => issue.message).join(' '));
    }

    final verifiedPackages = <String, _PackageReadResult>{};
    for (final action in plan.installActions.where(
      (action) => !action.enableOnly,
    )) {
      final actionPackage = action.root
          ? package
          : await _readPackage(
              action.packageUrl,
              expectedSha256: action.packageSha256,
            );
      if (actionPackage.manifest.id.toLowerCase() !=
              action.modId.toLowerCase() ||
          actionPackage.manifest.version != action.version) {
        throw StateError(
          'Package for ${action.modId} ${action.version} contains '
          '${actionPackage.manifest.id} ${actionPackage.manifest.version}.',
        );
      }
      final manifestErrors = actionPackage.manifest
          .validate()
          .where((issue) => issue.isBlocking)
          .toList(growable: false);
      if (manifestErrors.isNotEmpty) {
        throw StateError(
          'Package for ${action.modId} ${action.version} has an invalid '
          'manifest: ${manifestErrors.map((issue) => issue.message).join(' ')}',
        );
      }
      if (_canonicalPackageManifest(actionPackage.manifest) !=
          _canonicalPackageManifest(action.expectedManifest)) {
        throw StateError(
          'TFPKG170: Package for ${action.modId} ${action.version} contains a '
          'manifest that does not exactly match the package-source manifest '
          'approved by the install plan. Refresh the source and retry; the '
          'package was not staged.',
        );
      }
      verifiedPackages[action.modId.toLowerCase()] = actionPackage;
    }

    final commitInstall = await _validateGameDirectory(currentInstall.path);
    if (commitInstall.gameVersion != currentInstall.gameVersion) {
      throw StateError(
        'The installed Robotopia build changed while packages were being '
        'verified. Review the install plan again before installing.',
      );
    }

    final state = await _readManagerState(commitInstall);
    final installedById = {
      for (final mod in installed) mod.id.toLowerCase(): mod,
    };
    final staged = <_StagedPackageInstall>[];
    var stateSaved = false;
    try {
      for (final action in plan.installActions) {
        if (action.enableOnly) {
          final manifest = installedById[action.modId.toLowerCase()]?.manifest;
          if (manifest == null) {
            throw StateError(
              'Cannot enable ${action.modId}; installed manifest is missing.',
            );
          }
          _upsertState(state, manifest, enabled: true, restartRequired: true);
          continue;
        }
        final actionPackage = verifiedPackages[action.modId.toLowerCase()]!;
        staged.add(
          await _stagePackageInstall(
            actionPackage,
            commitInstall,
            source: _packageReceiptSource(
              reference: actionPackage.reference,
              sourceId: action.sourceId,
              sourceKind: action.root ? rootSourceKind : 'registry',
            ),
          ),
        );
        _upsertState(
          state,
          actionPackage.manifest,
          enabled: true,
          restartRequired: true,
          preserveExistingEnabled: action.root,
        );
      }
      for (var index = 0; index < staged.length; index++) {
        staged[index].commit();
        final hook = _packageInstallCommitHook;
        if (hook != null) {
          await hook(index + 1);
        }
      }
      await _saveManagerState(commitInstall, state);
      stateSaved = true;
    } on Object catch (error, stackTrace) {
      Object? rollbackError;
      for (final install in staged.reversed) {
        try {
          install.rollback();
        } on Object catch (current) {
          rollbackError ??= current;
        }
      }
      if (rollbackError != null) {
        Error.throwWithStackTrace(
          StateError(
            'Package install failed ($error), and rollback was incomplete '
            '($rollbackError).',
          ),
          stackTrace,
        );
      }
      rethrow;
    } finally {
      for (final install in staged) {
        install.clean(stateSaved: stateSaved);
      }
    }
    await _appendLauncherLogBestEffort(
      'Installed ${plan.installActions.length} package(s) for ${package.manifest.id} from $packagePath.',
    );
    return _loadInstalledMods(commitInstall);
  }

  Future<_StagedPackageInstall> _stagePackageInstall(
    _PackageReadResult package,
    GameInstall install, {
    required String source,
  }) async {
    if (!ModManifest.isValidId(package.manifest.id)) {
      throw StateError('Unsafe package id: ${package.manifest.id}.');
    }
    final packagesRoot = _packagesRoot(install);
    final transactionRoot = (_managerStaging(
      install,
    )..createSync(recursive: true)).createTempSync('launcher-install-');
    final target = Directory(
      p.join(packagesRoot.path, package.manifest.id, package.manifest.version),
    );
    final staging = Directory(p.join(transactionRoot.path, 'staging'))
      ..createSync(recursive: true);
    try {
      package.archive.extractTo(staging);
      await _validatePackageMetadataBeforeCommit(staging);
      await _writePackageInstallReceipt(staging, package, source);
    } on Object {
      if (transactionRoot.existsSync()) {
        transactionRoot.deleteSync(recursive: true);
      }
      rethrow;
    }
    return _StagedPackageInstall(
      target: target,
      staging: staging,
      backup: Directory(p.join(transactionRoot.path, 'backup')),
      transactionRoot: transactionRoot,
      targetParentExisted: target.parent.existsSync(),
    );
  }
}

String _canonicalPackageManifest(ModManifest manifest) =>
    _canonicalPackageJson(manifest.toJson());

String _canonicalPackageJson(Object? value) {
  if (value is Map) {
    final keys = value.keys.map((key) => key.toString()).toList()..sort();
    return '{${[for (final key in keys) '${jsonEncode(key)}:${_canonicalPackageJson(value[key])}'].join(',')}}';
  }
  if (value is List) {
    return '[${value.map(_canonicalPackageJson).join(',')}]';
  }
  return jsonEncode(value);
}

class _StagedPackageInstall {
  _StagedPackageInstall({
    required this.target,
    required this.staging,
    required this.backup,
    required this.transactionRoot,
    required this.targetParentExisted,
  });

  final Directory target;
  final Directory staging;
  final Directory backup;
  final Directory transactionRoot;
  final bool targetParentExisted;
  bool committed = false;

  void commit() {
    final targetType = FileSystemEntity.typeSync(
      target.path,
      followLinks: false,
    );
    if (targetType == FileSystemEntityType.link) {
      throw StateError('Refusing to replace symbolic link: ${target.path}');
    }
    if (targetType != FileSystemEntityType.notFound &&
        targetType != FileSystemEntityType.directory) {
      throw StateError('Package target is not a directory: ${target.path}');
    }
    target.parent.createSync(recursive: true);
    if (targetType == FileSystemEntityType.directory) {
      target.renameSync(backup.path);
    }
    try {
      staging.renameSync(target.path);
      committed = true;
    } on Object {
      if (!target.existsSync() && backup.existsSync()) {
        backup.renameSync(target.path);
      }
      rethrow;
    }
  }

  void rollback() {
    if (committed && target.existsSync()) {
      target.deleteSync(recursive: true);
    }
    if (!target.existsSync() && backup.existsSync()) {
      backup.renameSync(target.path);
    }
    committed = false;
  }

  void clean({required bool stateSaved}) {
    _deleteDirectoryBestEffort(staging);
    if (stateSaved) {
      _deleteDirectoryBestEffort(backup);
    }
    if (!backup.existsSync()) {
      _deleteDirectoryBestEffort(transactionRoot);
    }
    if (!targetParentExisted) {
      try {
        if (target.parent.existsSync() && target.parent.listSync().isEmpty) {
          target.parent.deleteSync();
        }
      } on Object {
        // Transaction cleanup is best-effort once commit/rollback has reached
        // a durable state. A stale empty directory must not change the result.
      }
    }
  }

  void _deleteDirectoryBestEffort(Directory directory) {
    try {
      if (directory.existsSync()) {
        directory.deleteSync(recursive: true);
      }
    } on Object {
      // Stale staging/backup directories can be removed by a later repair.
    }
  }
}
