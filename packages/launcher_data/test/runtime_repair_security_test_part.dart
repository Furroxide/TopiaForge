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
        for (final dll in [
          'TopiaForge.ModManager.dll',
          'TopiaForge.ModManager.Core.dll',
          'TopiaForge.Mods.Abstractions.dll',
          'TopiaForge.Mods.UnityUi.dll',
        ])
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
}
