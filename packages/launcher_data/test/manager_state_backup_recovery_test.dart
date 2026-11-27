import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// The game's manager state, which the launcher saves the same way as its
/// own files: what an interrupted save of it moved aside is put back before
/// the state is read, and while that cannot be done nothing changes it.
void main() {
  late Directory root;
  late Directory game;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-state-backups-')
          .resolveSymbolicLinksSync(),
    );
    game = Directory(p.join(root.path, 'game'))..createSync();
  });
  tearDown(() => root.deleteSync(recursive: true));

  LocalLauncherRepository repository() {
    final created = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      knownGamePath: p.join(root.path, 'missing-game'),
      // As while the game runs: restart requirements stay as they are, and
      // no process is looked for.
      gameRunningProbe: (_) async => true,
    );
    addTearDown(created.dispose);
    return created;
  }

  GameInstall install() => GameInstall(
    path: game.path,
    executablePath: p.join(game.path, 'game.exe'),
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  Directory managerFolder() =>
      Directory(p.join(game.path, 'BepInEx', 'TopiaForge'));
  File state() => File(p.join(managerFolder().path, 'state.json'));

  /// What the player chose: one mod off and one on.
  File storeChosen() => state()
    ..parent.createSync(recursive: true)
    ..writeAsStringSync(
      jsonEncode({
        'mods': [
          {'id': 'sam.mod', 'enabled': false},
          {'id': 'alex.mod', 'enabled': true},
        ],
      }),
    );

  /// Each mod in the state at [file], and whether it is enabled.
  Map<String, bool> enabledIn(File file) => {
    for (final mod in (jsonDecode(file.readAsStringSync()) as Map)['mods'])
      (mod as Map)['id'] as String: mod['enabled'] as bool,
  };

  File interrupt(File file) =>
      file.renameSync('${file.path}.4242-1760520615100000.bak');

  test('an interrupted save is put back before the state is read', () async {
    storeChosen();
    interrupt(state());
    await repository().setModEnabled(install(), 'alex.mod', false);
    expect(enabledIn(state()), {'sam.mod': false, 'alex.mod': false});
    expect(
      managerFolder().listSync().map((entity) => p.basename(entity.path)),
      unorderedEquals(['packages', 'state.json']),
    );
  });

  test('a backup held by another program changes nothing and loses '
      'nothing', () async {
    final original = storeChosen().readAsBytesSync();
    final backup = interrupt(state());
    final repo = repository();
    final release = await holdInPlace(backup);
    if (release == null) return;
    final unreadable = throwsA(
      isA<FileSystemException>().having(
        (error) => error.toString(),
        'message',
        startsWith(
          'Manager state could not be read just now, so nothing was changed.',
        ),
      ),
    );
    await expectLater(
      repo.setModEnabled(install(), 'alex.mod', false),
      unreadable,
    );
    await expectLater(repo.disableAllMods(install()), unreadable);
    expect(state().existsSync(), isFalse);
    await release();
    expect(backup.readAsBytesSync(), original);

    await repo.setModEnabled(install(), 'alex.mod', false);
    expect(enabledIn(state()), {'sam.mod': false, 'alex.mod': false});
  });

  test('is not looked for where no folder can be', () async {
    // A game the runtime was never installed into has no folder yet.
    await repository().setModEnabled(install(), 'sam.mod', false);
    expect(enabledIn(state()), isEmpty);

    // A file in the folder's place, and a file in place of its parent: the
    // state is missing, not unreadable, and saving it fails on its own.
    final notUnreadable = throwsA(
      isA<FileSystemException>().having(
        (error) => error.toString(),
        'message',
        isNot(contains('could not be read just now')),
      ),
    );
    final bepInEx = Directory(p.join(game.path, 'BepInEx'));
    managerFolder().deleteSync(recursive: true);
    File(managerFolder().path).writeAsStringSync('');
    await expectLater(
      repository().setModEnabled(install(), 'sam.mod', false),
      notUnreadable,
    );
    bepInEx.deleteSync(recursive: true);
    File(bepInEx.path).writeAsStringSync('');
    await expectLater(
      repository().setModEnabled(install(), 'sam.mod', false),
      notUnreadable,
    );
  });
}
