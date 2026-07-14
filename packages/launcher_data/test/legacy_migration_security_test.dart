import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory game;
  late Directory output;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('robotopia-legacy-security-');
    game = Directory(p.join(root.path, 'game'))..createSync();
    output = Directory(p.join(root.path, 'output'))..createSync();
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: p.join(root.path, 'repo'),
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test(
    'folder migration rejects nested links and preserves destination',
    () async {
      final source = _legacyFolder(game, 'safe.mod');
      final outside = File(p.join(root.path, 'outside.dll'))
        ..writeAsStringSync('outside');
      Link(p.join(source.path, 'linked.dll')).createSync(outside.path);
      final destination = Directory(p.join(output.path, 'safe.mod'))
        ..createSync();
      final sentinel = File(p.join(destination.path, 'keep.txt'))
        ..writeAsStringSync('keep');

      await expectLater(
        repository.migrateLegacyMods(game.path, output.path),
        throwsA(
          predicate(
            (error) => error.toString().contains('symbolic link or special'),
          ),
        ),
      );

      expect(sentinel.readAsStringSync(), 'keep');
      expect(outside.readAsStringSync(), 'outside');
      expect(_migrationTransactions(output), isEmpty);
    },
    skip: Platform.isWindows
        ? 'Windows symlink creation needs privilege.'
        : false,
  );

  test(
    'folder migration stages then atomically replaces destination',
    () async {
      final source = _legacyFolder(game, 'safe.mod');
      File(p.join(source.path, 'payload.dll')).writeAsStringSync('new payload');
      final destination = Directory(p.join(output.path, 'safe.mod'))
        ..createSync();
      File(p.join(destination.path, 'old.txt')).writeAsStringSync('old');

      final result = await repository.migrateLegacyMods(game.path, output.path);

      expect(result.createdProjects, [destination.path]);
      expect(File(p.join(destination.path, 'old.txt')).existsSync(), isFalse);
      expect(
        File(p.join(destination.path, 'payload.dll')).readAsStringSync(),
        'new payload',
      );
      expect(
        File(p.join(destination.path, 'robotopia.project.json')).existsSync(),
        isTrue,
      );
      expect(_migrationTransactions(output), isEmpty);
    },
  );

  test('oversized source is rejected before replacing destination', () async {
    final source = _legacyFolder(game, 'safe.mod');
    final huge = File(
      p.join(source.path, 'huge.bin'),
    ).openSync(mode: FileMode.write);
    huge.truncateSync(512 * 1024 * 1024 + 1);
    huge.closeSync();
    final destination = Directory(p.join(output.path, 'safe.mod'))
      ..createSync();
    final sentinel = File(p.join(destination.path, 'keep.txt'))
      ..writeAsStringSync('keep');

    await expectLater(
      repository.migrateLegacyMods(game.path, output.path),
      throwsA(predicate((error) => error.toString().contains('size limit'))),
    );

    expect(sentinel.readAsStringSync(), 'keep');
    expect(_migrationTransactions(output), isEmpty);
  });
}

Directory _legacyFolder(Directory game, String id) {
  final source = Directory(p.join(game.path, 'Mods', 'Legacy'))
    ..createSync(recursive: true);
  File(p.join(source.path, 'robotopia.mod.json')).writeAsStringSync(
    jsonEncode({
      'schemaVersion': 2,
      'name': id,
      'displayName': 'Legacy Mod',
      'version': '1.0.0',
      'author': {'name': 'Tester'},
      'entryAssembly': 'Legacy.dll',
      'entryType': 'Legacy.Entry',
    }),
  );
  return source;
}

List<FileSystemEntity> _migrationTransactions(Directory output) => output
    .listSync(followLinks: false)
    .where(
      (entity) =>
          p.basename(entity.path).startsWith('.robotopia-legacy-migration-'),
    )
    .toList();
