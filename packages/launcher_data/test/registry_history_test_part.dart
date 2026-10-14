part of 'launcher_data_test.dart';

/// History rows that inline a manifest let an exact older version be found
/// and installed, but never become catalog tiles.
void _registerRegistryHistoryTests({
  required Directory Function() root,
  required LocalLauncherRepository Function() repository,
  required Directory Function() gameRoot,
}) {
  group('registry history', () {
    late File latest;
    late File older;

    File writeIndex({required bool withManifests}) {
      final folder = Directory(p.join(root().path, 'history-source'))
        ..createSync(recursive: true);
      latest = _createPackage(folder, id: 'history.mod', version: '2.0.0');
      older = _createPackage(folder, id: 'history.mod', version: '1.0.0');
      final mismatched = _createPackage(
        folder,
        id: 'history.mod',
        version: '0.8.0',
      );
      Map<String, Object?> manifest(String version) =>
          _manifestJson('history.mod', version);
      final index = File(p.join(folder.path, 'index.json'))
        ..writeAsStringSync(
          jsonEncode({
            'formatVersion': ModRegistryFormat.indexFormatVersion,
            'mods': [
              {
                'manifest': manifest('2.0.0'),
                'downloadUrl': p.basename(latest.path),
                'packageSha256': sha256Of(latest),
                'history': [
                  {
                    'version': '1.0.0',
                    'downloadUrl': p.basename(older.path),
                    'packageSha256': sha256Of(older),
                    if (withManifests) 'manifest': manifest('1.0.0'),
                    if (withManifests) 'packageBytes': older.lengthSync(),
                  },
                  // A row whose manifest names another version is ignored.
                  {
                    'version': '0.7.0',
                    'downloadUrl': p.basename(mismatched.path),
                    'packageSha256': sha256Of(mismatched),
                    'manifest': manifest('0.8.0'),
                  },
                  // So is a malformed one; neither fails the source.
                  {'version': 7, 'manifest': 'not a manifest'},
                ],
              },
            ],
          }),
        );
      return index;
    }

    Future<void> useSources(
      File index, {
      List<PackageSource> extra = const [],
    }) => repository().savePackageSources([
      PackageSource(
        id: 'test.history',
        name: 'History Source',
        url: index.uri.toString(),
      ),
      ...extra,
    ]);

    test('a history row never becomes a tile, even a newer one', () async {
      final install = await repository().selectGameDirectory(gameRoot().path);
      final folder = Directory(p.join(root().path, 'misordered'))..createSync();
      final current = _createPackage(folder, id: 'odd.mod', version: '1.0.0');
      final newer = _createPackage(folder, id: 'odd.mod', version: '3.0.0');
      final index = File(p.join(folder.path, 'index.json'))
        ..writeAsStringSync(
          jsonEncode({
            'formatVersion': ModRegistryFormat.indexFormatVersion,
            'mods': [
              {
                'manifest': _manifestJson('odd.mod', '1.0.0'),
                'downloadUrl': p.basename(current.path),
                'packageSha256': sha256Of(current),
                'history': [
                  {
                    'version': '3.0.0',
                    'downloadUrl': p.basename(newer.path),
                    'packageSha256': sha256Of(newer),
                    'manifest': _manifestJson('odd.mod', '3.0.0'),
                  },
                ],
              },
            ],
          }),
        );
      await useSources(index);
      final snapshot = await repository().loadSnapshot();
      expect(
        snapshot.registryMods
            .where((mod) => mod.manifest.id == 'odd.mod')
            .map((mod) => mod.manifest.version),
        ['1.0.0'],
      );
      final main = _createPackage(
        root(),
        id: 'main.mod',
        version: '1.0.0',
        dependencies: [
          {'id': 'odd.mod', 'versionRange': '=3.0.0'},
        ],
      );
      final plan = await repository().previewPackage(main.path, install);
      expect(plan.hasBlockingIssues, isFalse, reason: '${plan.issues}');
      expect(
        plan.installActions
            .singleWhere((item) => item.modId == 'odd.mod')
            .version,
        '3.0.0',
      );
    });

    test('an exact older version installs from history', () async {
      final install = await repository().selectGameDirectory(gameRoot().path);
      await useSources(writeIndex(withManifests: true));
      final snapshot = await repository().loadSnapshot();
      final tiles = snapshot.registryMods
          .where((mod) => mod.manifest.id == 'history.mod')
          .toList();
      expect(tiles.map((mod) => mod.manifest.version), ['2.0.0']);
      expect(
        snapshot.sourceStatuses
            .singleWhere((status) => status.sourceId == 'test.history')
            .modCount,
        1,
      );

      final main = _createPackage(
        root(),
        id: 'main.mod',
        version: '1.0.0',
        dependencies: [
          {'id': 'history.mod', 'versionRange': '=1.0.0'},
        ],
      );
      final plan = await repository().previewPackage(main.path, install);
      expect(plan.hasBlockingIssues, isFalse, reason: '${plan.issues}');
      final action = plan.installActions.singleWhere(
        (item) => item.modId == 'history.mod',
      );
      expect(action.version, '1.0.0');
      expect(action.packageSha256, sha256Of(older));
      final mods = await repository().installPackage(main.path, install);
      expect(
        mods.singleWhere((mod) => mod.id == 'history.mod').version,
        '1.0.0',
      );
    });

    test(
      'history without manifests leaves the old version unavailable',
      () async {
        final install = await repository().selectGameDirectory(gameRoot().path);
        await useSources(writeIndex(withManifests: false));
        final main = _createPackage(
          root(),
          id: 'main.mod',
          version: '1.0.0',
          dependencies: [
            {'id': 'history.mod', 'versionRange': '=1.0.0'},
          ],
        );
        final plan = await repository().previewPackage(main.path, install);
        expect(plan.hasBlockingIssues, isTrue);
      },
    );

    test('the exact catalog holds installs and every source row', () async {
      final install = await repository().selectGameDirectory(gameRoot().path);
      final index = writeIndex(withManifests: true);
      final disabledFolder = Directory(p.join(root().path, 'disabled-source'))
        ..createSync();
      final offline = _createPackage(
        disabledFolder,
        id: 'offline.mod',
        version: '3.0.0',
      );
      await useSources(
        index,
        extra: [
          PackageSource(
            id: 'test.disabled',
            name: 'Disabled Folder',
            url: disabledFolder.path,
            enabled: false,
          ),
          PackageSource(
            id: 'test.remote',
            name: 'Disabled Remote',
            url: 'https://unreachable.invalid/index.json',
            enabled: false,
          ),
        ],
      );
      final installedPackage = _createPackage(
        root(),
        id: 'local.mod',
        version: '1.2.3',
      );
      await repository().installPackage(installedPackage.path, install);

      final catalog = await repository().loadExactPackageCatalog(install);
      expect(
        catalog.installed.map((item) => (item.sha256, item.manifest.id)),
        contains((sha256Of(installedPackage), 'local.mod')),
      );
      final rows = {
        for (final item in catalog.sources)
          '${item.sourceId}/${item.manifest.id}@${item.manifest.version}': item,
      };
      expect(rows['test.history/history.mod@2.0.0']!.sha256, sha256Of(latest));
      final history = rows['test.history/history.mod@1.0.0']!;
      expect(history.sha256, sha256Of(older));
      expect(history.sizeBytes, older.lengthSync());
      expect(history.enabled, isTrue);
      expect(rows.keys.where((key) => key.contains('@0.')), isEmpty);
      final disabled = rows['test.disabled/offline.mod@3.0.0']!;
      expect(disabled.enabled, isFalse);
      expect(disabled.sha256, sha256Of(offline));
      expect(rows.keys.where((key) => key.startsWith('test.remote/')), isEmpty);
      final log = File(p.join(repository().dataRoot, 'logs', 'launcher.log'));
      expect(
        log.existsSync() ? log.readAsStringSync() : '',
        isNot(contains('test.remote')),
      );

      final descriptor = ServerDescriptorV1(
        signingKey: WireText.tryParseBase64Url('A' * 43, 32)!,
        transportKey: WireText.tryParseBase64Url('A' * 43, 32)!,
        issuedAt: 0,
        name: 'History',
        gameBuild: '2545',
        runtimeVersion: '0.2.0',
        protocolVersion: '1.0.0',
        maxPlayers: 8,
        passwordRequired: false,
        voice: false,
        visibility: ServerVisibility.lan,
        hostServesPackages: false,
        gamemode: null,
        world: null,
        packages: [
          DescriptorPackage(
            id: 'history.mod',
            version: '1.0.0',
            sha256: sha256Of(older),
            role: DescriptorPackageRole.implicit,
          ),
          DescriptorPackage(
            id: 'local.mod',
            version: '1.2.3',
            sha256: sha256Of(installedPackage),
            role: DescriptorPackageRole.implicit,
          ),
          DescriptorPackage(
            id: 'offline.mod',
            version: '3.0.0',
            sha256: sha256Of(offline),
            role: DescriptorPackageRole.implicit,
          ),
        ],
      );
      final resolutions = catalog.resolve(descriptor, developerMode: false);
      expect(
        resolutions[sha256Of(older)]!.availability,
        JoinPackageAvailability.available,
      );
      expect(
        resolutions[sha256Of(installedPackage)]!.availability,
        JoinPackageAvailability.installed,
      );
      expect(
        resolutions[sha256Of(offline)]!.availability,
        JoinPackageAvailability.inDisabledSource,
      );
    });
  });
}
