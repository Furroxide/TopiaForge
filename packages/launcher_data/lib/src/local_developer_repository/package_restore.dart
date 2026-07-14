part of '../local_developer_repository.dart';

extension LocalDeveloperPackageRestore on LocalDeveloperRepository {
  Future<DeveloperLock> _restoreLockedPackages(
    String root,
    DeveloperLock lock,
  ) async {
    final transactionParent = Directory(p.join(root, '.topiaforge', 'staging'))
      ..createSync(recursive: true);
    final transaction = transactionParent.createTempSync('restore-');
    final swaps = <_StagedDeveloperDirectorySwap>[];
    final restored = <LockedPackage>[];
    final packageKeys = <String>{};
    var committed = false;
    try {
      for (var index = 0; index < lock.packages.length; index++) {
        final package = lock.packages[index];
        final id = _requireDeveloperPackageSegment(
          package.id,
          label: 'Package id',
        );
        final version = _requireDeveloperPackageSegment(
          package.version,
          label: 'Package version',
        );
        final packageKey = '$id/$version'.toLowerCase();
        if (!packageKeys.add(packageKey)) {
          throw StateError('Developer lock contains duplicate $id $version.');
        }

        final result = await _readPackage(
          package.packageUrl,
          expectedSha256: package.packageSha256,
        );
        if (result.manifest.id.toLowerCase() != id.toLowerCase() ||
            result.manifest.version != version) {
          throw StateError(
            'Package for $id $version contains '
            '${result.manifest.id} ${result.manifest.version}.',
          );
        }

        final staging = Directory(p.join(transaction.path, 'staging-$index'))
          ..createSync(recursive: true);
        final packageName = '$id-$version.topiaforgemod';
        final packageFile = File(p.join(staging.path, packageName));
        await packageFile.writeAsBytes(result.bytes, flush: true);
        _extractDeveloperArchive(
          result.archive,
          Directory(p.join(staging.path, 'extracted')),
          label: 'Package',
        );

        final target = Directory(
          p.join(root, '.topiaforge', 'packages', id, version),
        );
        swaps.add(
          _StagedDeveloperDirectorySwap(
            target: target,
            staging: staging,
            backup: Directory(p.join(transaction.path, 'backup-$index')),
          ),
        );
        restored.add(
          LockedPackage(
            id: package.id,
            name: package.name,
            version: package.version,
            packageUrl: package.packageUrl,
            packageSha256: result.sha256Hex,
            sourceId: package.sourceId,
            sourceName: package.sourceName,
            dependencies: package.dependencies,
            apiAssemblies: package.apiAssemblies,
            cachePath: p.relative(p.join(target.path, packageName), from: root),
          ),
        );
      }

      for (final swap in swaps) {
        swap.commit();
      }
      committed = true;
      return DeveloperLock(
        schemaVersion: lock.schemaVersion,
        projectId: lock.projectId,
        resolvedAtUtc: lock.resolvedAtUtc,
        packages: restored,
        dependencyGraph: lock.dependencyGraph,
      );
    } finally {
      if (!committed) {
        for (final swap in swaps.reversed) {
          swap.rollback();
        }
      }
      if (transaction.existsSync()) {
        transaction.deleteSync(recursive: true);
      }
      if (transactionParent.existsSync() &&
          transactionParent.listSync().isEmpty) {
        transactionParent.deleteSync();
      }
    }
  }
}
