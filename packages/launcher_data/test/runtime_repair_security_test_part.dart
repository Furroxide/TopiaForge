part of 'launcher_data_test.dart';

void _registerRuntimeRepairSecurityTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  test(
    'runtime repair rejects links in bundled source before copying',
    () async {
      final outside = File(
        p.join(repositoryRoot().parent.path, 'outside-runtime.dll'),
      )..writeAsStringSync('outside');
      final bundle = Directory(
        p.join(
          repositoryRoot().path,
          'third_party',
          'BepInEx',
          'win_x64_5.4.23.5',
        ),
      );
      Link(p.join(bundle.path, 'linked.dll')).createSync(outside.path);
      final install = await repository().selectGameDirectory(gameRoot().path);

      final report = await repository().installOrRepairRuntime(install);

      expect(report.ok, isFalse);
      expect(
        report.issues.map((issue) => issue.message).join(' '),
        contains('symbolic link'),
      );
      expect(
        File(p.join(gameRoot().path, 'winhttp.dll')).existsSync(),
        isFalse,
      );
      expect(outside.readAsStringSync(), 'outside');
    },
    skip: Platform.isWindows
        ? 'Windows symlink creation needs privilege.'
        : false,
  );

  test(
    'runtime repair refuses a linked destination parent',
    () async {
      final outside = Directory(
        p.join(repositoryRoot().parent.path, 'outside-plugins'),
      )..createSync();
      final sentinel = File(p.join(outside.path, 'keep.txt'))
        ..writeAsStringSync('keep');
      final bepinex = Directory(p.join(gameRoot().path, 'BepInEx'))
        ..createSync();
      Link(p.join(bepinex.path, 'plugins')).createSync(outside.path);
      final install = await repository().selectGameDirectory(gameRoot().path);

      final report = await repository().installOrRepairRuntime(install);

      expect(report.ok, isFalse);
      expect(
        report.issues.map((issue) => issue.message).join(' '),
        contains('symbolic link'),
      );
      expect(sentinel.readAsStringSync(), 'keep');
      expect(
        File(p.join(outside.path, 'TopiaForge.dll')).existsSync(),
        isFalse,
      );
    },
    skip: Platform.isWindows
        ? 'Windows symlink creation needs privilege.'
        : false,
  );

  test(
    'runtime repair rolls back every managed file when commit fails',
    () async {
      final managedTargets = <String, String>{
        'winhttp.dll': 'old proxy',
        'doorstop_config.ini': 'old config',
        p.join('BepInEx', 'core', 'BepInEx.dll'): 'old core',
        for (final dll in topiaForgeRuntimeLoaderDlls)
          p.join('BepInEx', 'plugins', 'TopiaForge.ModManager', dll):
              'old $dll',
      };
      for (final entry in managedTargets.entries) {
        final target = File(p.join(gameRoot().path, entry.key));
        target.parent.createSync(recursive: true);
        target.writeAsStringSync(entry.value);
      }
      final failing = LocalLauncherRepository(
        dataRoot: p.join(repositoryRoot().parent.path, 'rollback-data'),
        repositoryRoot: repositoryRoot().path,
        knownGamePath: gameRoot().path,
        runtimeRepairCommitHook: (committed) {
          if (committed == 2) {
            throw StateError('injected runtime commit failure');
          }
        },
      );
      final install = await failing.selectGameDirectory(gameRoot().path);

      final report = await failing.installOrRepairRuntime(install);

      expect(report.ok, isFalse);
      expect(
        report.issues.map((issue) => issue.message).join(' '),
        contains('injected runtime commit failure'),
      );
      for (final entry in managedTargets.entries) {
        expect(
          File(p.join(gameRoot().path, entry.key)).readAsStringSync(),
          entry.value,
          reason: entry.key,
        );
      }
      expect(
        Directory(
          p.join(gameRoot().path, '.topiaforge-runtime-transaction'),
        ).existsSync(),
        isFalse,
      );
    },
  );

  test(
    'runtime repair recovers an interrupted transaction before staging',
    () async {
      final target = File(p.join(gameRoot().path, 'winhttp.dll'))
        ..writeAsStringSync('original proxy');
      final transaction = Directory(
        p.join(gameRoot().path, '.topiaforge-runtime-transaction'),
      );
      final backup = File(p.join(transaction.path, 'backups', '0.bak'));
      backup.parent.createSync(recursive: true);
      target.renameSync(backup.path);
      target.writeAsStringSync('partially installed proxy');
      File(p.join(transaction.path, 'journal.json')).writeAsStringSync(
        jsonEncode({
          'formatVersion': 2,
          'status': 'committing',
          'operations': [
            {
              'relativePath': 'winhttp.dll',
              'phase': 'installed',
              'hadOriginal': true,
            },
          ],
        }),
      );
      Directory(
        p.join(repositoryRoot().path, 'third_party', 'BepInEx'),
      ).deleteSync(recursive: true);
      final install = await repository().selectGameDirectory(gameRoot().path);

      final report = await repository().installOrRepairRuntime(install);

      expect(report.ok, isFalse);
      expect(target.readAsStringSync(), 'original proxy');
      expect(transaction.existsSync(), isFalse);
    },
  );

  File installedNotice(String fileName) => File(
    p.join(
      gameRoot().path,
      'BepInEx',
      'plugins',
      'TopiaForge.ModManager',
      topiaForgeRuntimeLoaderNoticeDirectory,
      fileName,
    ),
  );
  // Commit order is the bundle, then the assemblies, then their notices, so
  // failing on the final operation fails after every notice has landed.
  int runtimeOperations() =>
      Directory(
        p.join(
          repositoryRoot().path,
          'third_party',
          'BepInEx',
          'win_x64_5.4.23.5',
        ),
      ).listSync(recursive: true).whereType<File>().length +
      topiaForgeRuntimeLoaderDlls.length +
      topiaForgeRuntimeLoaderNotices.length;
  LocalLauncherRepository failingOnLastOperation({void Function()? before}) {
    final operations = runtimeOperations();
    final failing = LocalLauncherRepository(
      dataRoot: p.join(repositoryRoot().parent.path, 'late-failure-data'),
      repositoryRoot: repositoryRoot().path,
      knownGamePath: gameRoot().path,
      runtimeRepairCommitHook: (committed) {
        if (committed == operations) {
          before?.call();
          throw StateError('injected late commit failure');
        }
      },
    );
    addTearDown(failing.dispose);
    return failing;
  }

  test('a failed first install leaves no file or folder behind', () async {
    List<String> gameTree() => [
      for (final entity in gameRoot().listSync(
        recursive: true,
        followLinks: false,
      ))
        // The repair lock persists by design; it is not runtime content.
        if (p.basename(entity.path) != '.topiaforge-runtime-repair.lock')
          p.relative(entity.path, from: gameRoot().path),
    ]..sort();
    final before = gameTree();
    var noticesLanded = false;
    final failing = failingOnLastOperation(
      before: () => noticesLanded = topiaForgeRuntimeLoaderNotices.every(
        (notice) => installedNotice(notice.fileName).existsSync(),
      ),
    );
    final install = await failing.selectGameDirectory(gameRoot().path);

    final report = await failing.installOrRepairRuntime(install);

    expect(report.ok, isFalse);
    expect(noticesLanded, isTrue);
    expect(gameTree(), before);
  });

  test('a failed repair restores the notices it replaced', () async {
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      installedNotice(notice.fileName)
        ..parent.createSync(recursive: true)
        ..writeAsStringSync('previous ${notice.fileName}');
    }
    final failing = failingOnLastOperation();
    final install = await failing.selectGameDirectory(gameRoot().path);

    final report = await failing.installOrRepairRuntime(install);

    expect(report.ok, isFalse);
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      expect(
        installedNotice(notice.fileName).readAsStringSync(),
        'previous ${notice.fileName}',
      );
    }
    expect(
      Directory(
        p.join(gameRoot().path, '.topiaforge-runtime-transaction'),
      ).existsSync(),
      isFalse,
    );
  });

  test(
    'recovery removes an interrupted notice and the folders it created',
    () async {
      final notice = installedNotice('LICENSE')
        ..parent.createSync(recursive: true)
        ..writeAsStringSync('partially installed notice');
      final transaction = Directory(
        p.join(gameRoot().path, '.topiaforge-runtime-transaction'),
      )..createSync();
      File(p.join(transaction.path, 'journal.json')).writeAsStringSync(
        jsonEncode({
          'formatVersion': 2,
          'status': 'committing',
          'operations': [
            {
              'relativePath':
                  'BepInEx/plugins/TopiaForge.ModManager/licenses/LICENSE',
              'phase': 'installed',
              'hadOriginal': false,
            },
          ],
          'createdDirectories': [
            'BepInEx',
            'BepInEx/plugins',
            'BepInEx/plugins/TopiaForge.ModManager',
            'BepInEx/plugins/TopiaForge.ModManager/licenses',
          ],
        }),
      );
      // Without a bundled runtime the repair stops right after recovery.
      Directory(
        p.join(repositoryRoot().path, 'third_party', 'BepInEx'),
      ).deleteSync(recursive: true);
      final install = await repository().selectGameDirectory(gameRoot().path);

      final report = await repository().installOrRepairRuntime(install);

      expect(report.ok, isFalse);
      expect(notice.existsSync(), isFalse);
      expect(
        Directory(p.join(gameRoot().path, 'BepInEx')).existsSync(),
        isFalse,
      );
      expect(transaction.existsSync(), isFalse);
    },
  );
}
