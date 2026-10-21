import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// The developer tools' files, the launcher log and the game-compatibility
/// cache are saved through the same writer as the launcher's other files, so
/// what a save stopped part way left is put back or swept by one rule. Only
/// the writer's own names count, and a `<name>.bak` the author keeps is never
/// put back or deleted.
void main() {
  late Directory root;
  final old = DateTime.now().subtract(const Duration(hours: 2));

  setUp(() => root = Directory.systemTemp.createTempSync('tf-leftovers-'));
  tearDown(() => root.deleteSync(recursive: true));

  String token(int processId, DateTime at) =>
      '$processId-${at.microsecondsSinceEpoch}';

  group('developer files', () {
    late LocalDeveloperRepository repository;
    late File projectFile;

    setUp(() async {
      repository = LocalDeveloperRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: root.path,
      );
      final workspace = await repository.createModProject(
        parentDirectory: root.path,
        id: 'test.leftovers',
        name: 'Leftovers',
      );
      projectFile = File(
        p.join(workspace.projectRoot, 'topiaforge.project.json'),
      );
    });

    String projectRoot() => projectFile.parent.path;
    File beside(String name) => File(p.join(projectRoot(), name));
    Future<String?> loadedId() async =>
        (await repository.loadDeveloperWorkspace(
          projectPath: projectRoot(),
        )).project?.id;

    test('an author\'s own `<name>.bak` is kept through reads and '
        'saves', () async {
      final mine = beside('topiaforge.project.json.bak')
        ..writeAsStringSync('mine');

      expect(await loadedId(), 'test.leftovers');
      await repository.addProjectDependency(
        projectRoot(),
        const ModDependency(id: 'test.other'),
      );

      expect(mine.readAsStringSync(), 'mine');
      expect(
        (await repository.loadDeveloperWorkspace(
          projectPath: projectRoot(),
        )).project?.dependencies.map((item) => item.id),
        ['test.other'],
      );
    });

    test('an author\'s own `<name>.bak` never stands in for a missing '
        'file', () async {
      final saved = projectFile.readAsStringSync();
      projectFile.deleteSync();
      final mine = beside('topiaforge.project.json.bak')
        ..writeAsStringSync(saved);

      expect(await loadedId(), isNull);
      expect(projectFile.existsSync(), isFalse);
      expect(mine.readAsStringSync(), saved);
    });

    test('a save stopped part way is put back from its own backup, and an '
        'author\'s `<name>.bak` beside it is left alone', () async {
      final saved = projectFile.readAsStringSync();
      // What a save stopped between its two moves leaves: the file moved
      // aside under the save's own name, and the new version, never moved
      // in, as its temporary file.
      final aside = projectFile.renameSync(
        beside('topiaforge.project.json.${token(4242, old)}.bak').path,
      );
      beside(
        'topiaforge.project.json.${token(4242, old)}.tmp',
      ).writeAsStringSync('{"unfinished": true}');
      final mine = beside('topiaforge.project.json.bak')
        ..writeAsStringSync('mine');

      expect(await loadedId(), 'test.leftovers');
      expect(projectFile.readAsStringSync(), saved);
      expect(aside.existsSync(), isFalse);
      expect(mine.readAsStringSync(), 'mine');
    });

    test('an author\'s own `<name>.bak` never stands in for a missing file, '
        'even beside their own `<name>.tmp`', () async {
      final saved = projectFile.readAsStringSync();
      projectFile.deleteSync();
      final mine = beside('topiaforge.project.json.bak')
        ..writeAsStringSync(saved);
      final draft = beside('topiaforge.project.json.tmp')
        ..writeAsStringSync('{');

      expect(await loadedId(), isNull);
      expect(projectFile.existsSync(), isFalse);
      expect(mine.readAsStringSync(), saved);
      expect(draft.readAsStringSync(), '{');
    });

    test('a save stopped part way is put back from the newest of its '
        'backups', () async {
      final newer = projectFile.renameSync(
        beside(
          'topiaforge.project.json.${token(4242, DateTime(2026, 10, 3))}.bak',
        ).path,
      )..setLastModifiedSync(DateTime(2026, 10, 3));
      final older =
          beside(
              'topiaforge.project.json.${token(4242, DateTime(2026, 10, 1))}.bak',
            )
            ..writeAsStringSync('{"older": true}')
            ..setLastModifiedSync(DateTime(2026, 10, 1));

      expect(await loadedId(), 'test.leftovers');
      expect(newer.existsSync(), isFalse);
      expect(older.existsSync(), isFalse);
    });

    test('a held backup leaves the project unread, and nothing '
        'changes', () async {
      final backup = projectFile.renameSync(
        beside('topiaforge.project.json.${token(4242, old)}.bak').path,
      );
      if (await holdInPlace(backup) == null) return;

      await expectLater(
        repository.loadDeveloperWorkspace(projectPath: projectRoot()),
        throwsA(
          isA<FileSystemException>().having(
            (error) => error.message,
            'message',
            startsWith(
              'topiaforge.project.json could not be read just now, so '
              'nothing was changed.',
            ),
          ),
        ),
      );
      expect(backup.existsSync(), isTrue);
      expect(projectFile.existsSync(), isFalse);
    });

    test('the search for a project passes over a folder that cannot be '
        'listed', () async {
      final locked = Directory(p.join(projectRoot(), 'locked'))..createSync();
      final inner = Directory(p.join(locked.path, 'inner'))..createSync();
      if (await denyListing(locked) == null) return;

      final workspace = await repository.loadDeveloperWorkspace(
        projectPath: inner.path,
      );

      expect(workspace.project?.id, 'test.leftovers');
    });

    test('a save sweeps the old temporary files of its file, and leaves '
        'none of its own', () async {
      final stale = beside('topiaforge.project.json.${token(7777, old)}.tmp')
        ..writeAsStringSync('partial')
        ..setLastModifiedSync(old);
      final mine = beside('topiaforge.project.json.tmp')
        ..writeAsStringSync('mine');

      await repository.addProjectDependency(
        projectRoot(),
        const ModDependency(id: 'test.other'),
      );

      expect(stale.existsSync(), isFalse);
      expect(mine.readAsStringSync(), 'mine');
      expect(
        Directory(projectRoot())
            .listSync()
            .map((entity) => p.basename(entity.path))
            .where((name) => name.startsWith('topiaforge.project.json')),
        unorderedEquals([
          'topiaforge.project.json',
          'topiaforge.project.json.tmp',
        ]),
      );
    });
  });

  group('the launcher log', () {
    late Directory dataRoot;
    late Directory logs;
    late File log;

    setUp(() {
      dataRoot = Directory(p.join(root.path, 'data'))..createSync();
      logs = Directory(p.join(dataRoot.path, 'logs'))..createSync();
      log = File(p.join(logs.path, 'launcher.log'));
    });

    LocalLauncherRepository repository() {
      final created = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: root.path,
        knownGamePath: p.join(root.path, 'missing-game'),
      );
      addTearDown(created.dispose);
      return created;
    }

    test('the launcher\'s own files never read a `<name>.bak`, even beside '
        'a temporary file', () async {
      final settings = File(p.join(dataRoot.path, 'settings.json'));
      final mine = File('${settings.path}.bak')
        ..writeAsStringSync('{"developerMode": true}');
      File(
        '${settings.path}.${token(4242, DateTime.now())}.tmp',
      ).writeAsStringSync('{"partial');

      final snapshot = await repository().loadSnapshot();

      expect(snapshot.developerMode, isFalse);
      expect(settings.existsSync(), isFalse);
      expect(mine.readAsStringSync(), '{"developerMode": true}');
    });

    test('a rotation stopped part way is put back before the log is '
        'read', () async {
      final aside = File(
        p.join(logs.path, 'launcher.log.${token(4242, old)}.bak'),
      )..writeAsStringSync('2026-10-19T00:00:00.000Z Before the rotation.\n');

      final snapshot = await repository().loadSnapshot();

      expect(snapshot.launcherLog, contains('Before the rotation.'));
      expect(log.existsSync(), isTrue);
      expect(aside.existsSync(), isFalse);
    });

    test('old leftovers of stopped rotations beside a log are swept once, '
        'and recent ones kept', () async {
      log.writeAsStringSync('2026-10-19T00:00:00.000Z Kept.\n');
      final stale = [
        File(p.join(logs.path, 'launcher.log.${token(4242, old)}.bak')),
        File(p.join(logs.path, 'launcher.log.${token(4243, old)}.tmp')),
      ];
      for (final file in stale) {
        file
          ..writeAsStringSync('old')
          ..setLastModifiedSync(old);
      }
      final recent = File(
        p.join(logs.path, 'launcher.log.${token(4244, DateTime.now())}.bak'),
      )..writeAsStringSync('recent');
      final mine = File(p.join(logs.path, 'launcher.log.bak'))
        ..writeAsStringSync('mine')
        ..setLastModifiedSync(old);

      await repository().loadSnapshot();

      for (final file in stale) {
        expect(file.existsSync(), isFalse, reason: p.basename(file.path));
      }
      expect(recent.existsSync(), isTrue);
      expect(mine.existsSync(), isTrue);
      expect(log.readAsStringSync(), contains('Kept.'));
    });

    test(
      'a held backup never stops logging',
      () async {
        final aside = File(
          p.join(logs.path, 'launcher.log.${token(4242, old)}.bak'),
        )..writeAsStringSync('2026-10-19T00:00:00.000Z Held.\n');
        if (await holdInPlace(aside) == null) return;
        final game = _createGame(Directory(p.join(root.path, 'game')));

        await repository().selectGameDirectory(game.path);

        expect(log.readAsStringSync(), contains('Selected game directory'));
        expect(aside.existsSync(), isTrue);
      },
      skip: Platform.isWindows
          ? false
          : 'Holding a file in place elsewhere freezes its whole folder, so '
                'no new log could be written there either.',
    );
  });

  test('a game-compatibility cache save stopped part way is put back, and '
      'old leftovers beside it are swept', () async {
    final game = _createGame(Directory(p.join(root.path, 'game')));
    final gameCode = File(
      p.join(game.path, 'Robotopia_Data', 'Managed', 'GameCode.dll'),
    )..writeAsStringSync('game code');
    final cacheFolder = Directory(p.join(game.path, 'BepInEx', 'TopiaForge'))
      ..createSync(recursive: true);
    final verdict = GameCompatStatus(
      status: 'ok',
      gameCodeSha: sha256.convert(gameCode.readAsBytesSync()).toString(),
    );
    final aside = File(
      p.join(cacheFolder.path, 'compat-status.json.${token(4242, old)}.bak'),
    )..writeAsStringSync(jsonEncode(verdict.toJson()));
    final stale =
        File(
            p.join(
              cacheFolder.path,
              'compat-status.json.${token(4243, old)}.tmp',
            ),
          )
          ..writeAsStringSync('partial')
          ..setLastModifiedSync(old);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    addTearDown(repository.dispose);

    final install = await repository.selectGameDirectory(game.path);

    // No extractor is bundled here, so only the cache can say 'ok'.
    expect(install.compatStatus?.status, 'ok');
    expect(aside.existsSync(), isFalse);
    expect(stale.existsSync(), isFalse);
    expect(
      File(p.join(cacheFolder.path, 'compat-status.json')).existsSync(),
      isTrue,
    );
  });
}

Directory _createGame(Directory root) {
  File(p.join(root.path, 'Robotopia.exe'))
    ..createSync(recursive: true)
    ..writeAsStringSync('');
  final managed = Directory(p.join(root.path, 'Robotopia_Data', 'Managed'))
    ..createSync(recursive: true);
  File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
  return root;
}
