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
      loaderVersion: LocalLauncherRepository._loaderVersion,
      sdkVersion: LocalLauncherRepository._sdkVersion,
    );
  }

  Future<List<InstalledMod>> _installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
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
      availableMods: registryMods,
      gameVersion: currentInstall.gameVersion,
      requireKnownGameVersion: true,
      loaderVersion: LocalLauncherRepository._loaderVersion,
      sdkVersion: LocalLauncherRepository._sdkVersion,
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
        staged.add(_stagePackageInstall(actionPackage, commitInstall));
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
    await _appendLauncherLog(
      'Installed ${plan.installActions.length} package(s) for ${package.manifest.id} from $packagePath.',
    );
    return _loadInstalledMods(commitInstall);
  }

  _StagedPackageInstall _stagePackageInstall(
    _PackageReadResult package,
    GameInstall install,
  ) {
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
