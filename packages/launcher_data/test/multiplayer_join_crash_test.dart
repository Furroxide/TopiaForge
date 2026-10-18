import 'dart:convert';
import 'dart:io';
import 'dart:isolate';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// Crash safety: a launcher killed part way through a join's install leaves
/// nothing the game or the player's profiles load, and its next start
/// finishes or undoes the install.
void main() {
  /// Runs the join's install in another process that dies at [step].
  Future<ProcessResult> killedAt(JoinRig rig, String step) async {
    final library = await Isolate.resolvePackageUri(
      Uri.parse('package:launcher_data/launcher_data.dart'),
    );
    final packageRoot = File.fromUri(library!).parent.parent.path;
    return Process.run(Platform.resolvedExecutable, [
      'run',
      p.join(packageRoot, 'test', 'multiplayer_join_crash_child.dart'),
      rig.game.path,
      rig.dataRoot,
      rig.repositoryRoot.path,
      step,
    ], workingDirectory: packageRoot);
  }

  Future<JoinRig> playerWithDownloads() async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    rig.cache(StandardJoin.downloads);
    return rig;
  }

  test('killed between stage and commit, nothing loads and the next start '
      'sweeps it', () async {
    final rig = await playerWithDownloads();
    final before = await rig.everything(StandardJoin.downloads);
    final state = managerStateFile(rig.game).readAsBytesSync();
    final killed = await killedAt(rig, 'staged');
    expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');

    // Nothing the next start would load: no package folder, the manager state
    // byte for byte, no record of server content.
    expect(managerStateFile(rig.game).readAsBytesSync(), state);
    for (final archive in StandardJoin.downloads) {
      expect(
        packageFolder(rig.game, archive.id, archive.version).existsSync(),
        isFalse,
      );
    }
    expect(serverContentFile(rig.game).existsSync(), isFalse);
    expect(joinFolders(rig.game), hasLength(1));

    // The next start sweeps what the killed install left.
    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
  });

  // Every step of the order that changes what the game reads: the manager
  // records, the record of server content, and each package but the last.
  for (final (step, placed) in [
    ('state', 0),
    ('record', 0),
    ('committed 1', 1),
    ('committed 2', 2),
  ]) {
    test('killed at "$step", nothing new loads and the next start undoes '
        'it', () async {
      final rig = await playerWithDownloads();
      final before = await rig.everything(StandardJoin.downloads);
      final profiles = [
        for (final text in rig.storedProfiles().values)
          LauncherProfile.fromJson(jsonDecode(text) as Map<String, Object?>),
      ];
      final loadsBefore = {
        for (final profile in profiles) profile.id: await rig.loadsOf(profile),
      };
      final killed = await killedAt(rig, step);
      expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');

      // Packages may be in place, but no profile of the player's own loads
      // them, and the mod new to the player is recorded off, so a game
      // started without the launcher does not turn it on either. (A preview
      // runs no sweep; a snapshot does.)
      expect(
        [
          for (final archive in StandardJoin.downloads)
            packageFolder(rig.game, archive.id, archive.version).existsSync(),
        ].where((inPlace) => inPlace),
        hasLength(placed),
      );
      for (final profile in profiles) {
        expect(await rig.loadsOf(profile), loadsBefore[profile.id]);
      }
      expect(rig.storedProfiles(), before['profiles']);
      final records = managerRecords(rig.game);
      expect(jsonDecode(records['server.delta']!)['enabled'], isFalse);
      expect(joinFolders(rig.game), hasLength(1));

      // The next start undoes it.
      rig.repository = rig.open();
      expect(await rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(rig.game), isEmpty);
    });
  }

  test('killed part way, the next start waits while a receipt it would judge '
      'by cannot be read, then undoes it', () async {
    final rig = await playerWithDownloads();
    final before = await rig.everything(StandardJoin.downloads);
    final killed = await killedAt(rig, 'committed 2');
    expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
    final release = await holdUnreadable(
      File(
        p.join(
          packageFolder(rig.game, 'own.alpha', '2.0.0').path,
          'topiaforge.install.json',
        ),
      ),
    );
    if (release == null) return;
    rig.repository = rig.open();
    await rig.repository.loadSnapshot();
    // Nothing was put back: the server's Alpha keeps its record entry, so it
    // stays hidden, and the journal waits.
    expect(joinFolders(rig.game), hasLength(1));
    final record = ServerContentRecord.read(
      serverContentFile(rig.game).readAsBytesSync(),
    ).record!;
    expect(record.entryFor('own.alpha', '2.0.0')!.hidden, isTrue);
    await release();
    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
  });

  test('killed once every package was in place, the next start keeps the '
      'install', () async {
    final rig = await playerWithDownloads();
    final killed = await killedAt(rig, 'committed 3');
    expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
    rig.repository = rig.open();
    await rig.repository.loadSnapshot();
    expect(joinFolders(rig.game), isEmpty);
    for (final archive in StandardJoin.downloads) {
      expect(
        packageFolder(rig.game, archive.id, archive.version).existsSync(),
        isTrue,
      );
    }
    final view = await rig.playerView();
    expect(view.mods, contains('server.delta@1.0.0 off 1.0.0'));
  });
}
