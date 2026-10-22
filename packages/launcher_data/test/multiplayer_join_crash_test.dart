import 'dart:convert';
import 'dart:io';
import 'dart:isolate';

import 'package:crypto/crypto.dart';
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

  test('killed part way while a save of its journal had moved the journal '
      'aside, the next start puts it back and undoes the install as with the '
      'journal in place', () async {
    // The same stop twice: with the journal in place, and as a save of it
    // that stopped between its two moves leaves it on Windows, where a save
    // that cannot replace the journal in one move moves it aside first. Both
    // end with everything as the player had it before the join.
    for (final onlyBackup in [false, true]) {
      final rig = await playerWithDownloads();
      final before = await rig.everything(StandardJoin.downloads);
      final killed = await killedAt(rig, 'committed 2');
      expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
      if (onlyBackup) _leaveOnlyBackup(_journalOf(rig.game));

      rig.repository = rig.open();
      expect(
        await rig.everything(StandardJoin.downloads),
        before,
        reason: 'only the backup: $onlyBackup',
      );
      expect(joinFolders(rig.game), isEmpty);
    }
  });

  for (final (held, how) in [
    ('backup', 'is left only as a backup another program holds'),
    ('journal', 'cannot be read'),
  ]) {
    test(
      'killed part way, while its journal $how, the next start changes '
      'nothing, a new join is refused, and a later start undoes it',
      () async {
        final rig = await playerWithDownloads();
        final before = await rig.everything(StandardJoin.downloads);
        final killed = await killedAt(rig, 'committed 2');
        expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
        final journal = _journalOf(rig.game);
        final release = held == 'backup'
            ? await holdInPlace(_leaveOnlyBackup(journal))
            : await holdUnreadable(journal);
        if (release == null) return;
        final left = _gameFiles(rig.game);

        // Nothing is deleted or undone: the packages it put in place, its
        // record entries, the manager record it added, and its folder and
        // journal all stay for a later start.
        rig.repository = rig.open();
        await rig.repository.loadSnapshot();
        expect(_gameFiles(rig.game), left);
        final waiting = _launcherLog(
          rig,
        ).split('\n').where((line) => line.contains('settled yet')).toList();
        expect(waiting, isNotEmpty);
        expect(
          waiting,
          everyElement(
            allOf(
              contains(
                'A stopped join install could not be settled yet: '
                '$_journalLabel could not be read just now, so nothing was '
                'changed.',
              ),
              isNot(contains(rig.root.path)),
            ),
          ),
        );

        // A new join is refused, as is planning one, with the message the
        // launcher gives for a file of its own it cannot read just now.
        final cause = held == 'backup'
            ? 'A save of it was interrupted, and the backup that save left '
                  'could not be put back'
            : 'Another program may be using it';
        final (server, plan) = await StandardJoin.plan();
        final refused = await rig.repository.installJoinContent(
          rig.install,
          installingJoin(server, plan),
        );
        expect(refused, isA<JoinFailed>());
        expect((refused as JoinFailed).rollbackComplete, isTrue);
        expect(refused.detail, _unreadableJournal(rig, cause));
        expect(
          refused.detail,
          endsWith(
            'Close that program or restore access, then try again. Nothing was '
            'installed.',
          ),
        );
        await expectLater(
          rig.repository.loadExactPackageCatalog(rig.install),
          throwsA(
            isA<FileSystemException>().having(
              (error) => error.toString(),
              'message',
              _unreadableJournal(rig, cause),
            ),
          ),
        );
        expect(_gameFiles(rig.game), left);

        // Once the hold is gone, a later start undoes it.
        await release();
        rig.repository = rig.open();
        expect(await rig.everything(StandardJoin.downloads), before);
        expect(joinFolders(rig.game), isEmpty);
      },
    );
  }

  test('killed part way, a journal that is there but damaged still lets its '
      'folder go, as before', () async {
    final rig = await playerWithDownloads();
    final killed = await killedAt(rig, 'committed 2');
    expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
    final journal = _journalOf(rig.game);
    journal.writeAsStringSync(
      journal.readAsStringSync().substring(0, 20),
      flush: true,
    );

    rig.repository = rig.open();
    await rig.repository.loadSnapshot();
    expect(joinFolders(rig.game), isEmpty);
    await rig.repository.loadExactPackageCatalog(rig.install);
  });

  test('killed before its journal was saved, the next start still sweeps '
      'its folder', () async {
    final rig = await playerWithDownloads();
    final before = await rig.everything(StandardJoin.downloads);
    final killed = await killedAt(rig, 'staged');
    expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
    _journalOf(rig.game).deleteSync();

    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
  });

  // The catalog takes no staging lease, so another launcher's install can be
  // saving the journal while it reads; on Windows a save that cannot replace
  // the journal in one move moves it aside first.
  for (final (moment, afterTheLook) in [('before', false), ('after', true)]) {
    test('killed part way, a journal that a save moves aside just $moment the '
        'catalog looks for it is not taken for none', () async {
      final rig = await playerWithDownloads();
      final killed = await killedAt(rig, 'committed 2');
      expect(killed.exitCode, 86, reason: '${killed.stdout}${killed.stderr}');
      final journal = _journalOf(rig.game);
      final folder = joinFolders(rig.game).single;
      rig.repository = rig.open();

      // The recovery finds the journal at the first look; the save moves it
      // aside around the second, the read's own.
      var looks = 0;
      await expectLater(
        IOOverrides.runZoned(
          () => rig.repository.loadExactPackageCatalog(rig.install),
          fseGetTypeSync: (path, followLinks) {
            FileSystemEntityType look() => IOOverrides.runWithIOOverrides(
              () => FileSystemEntity.typeSync(path, followLinks: followLinks),
              _plain,
            );
            if (p.basename(path) != 'journal.json' ||
                p.basename(p.dirname(path)) != folder ||
                ++looks != 2) {
              return look();
            }
            final seen = afterTheLook ? look() : null;
            IOOverrides.runWithIOOverrides(
              () => _leaveOnlyBackup(journal),
              _plain,
            );
            return seen ?? look();
          },
        ),
        throwsA(
          isA<FileSystemException>().having(
            (error) => error.toString(),
            'message',
            _unreadableJournal(rig, 'Another program may be using it'),
          ),
        ),
      );
    });
  }

  test('a join whose own journal another program holds once it is saved '
      'goes ahead, since only an earlier join can be unsettled', () async {
    late JoinRig rig;
    rig = await JoinRig.create(
      hook: (step) async {
        if (step == 'staged') await holdUnreadable(_journalOf(rig.game));
      },
    );
    await preparePlayer(rig);
    rig.cache(StandardJoin.downloads);
    final (server, plan) = await StandardJoin.plan();
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    expect(
      event,
      isA<JoinInstallCompleted>(),
      reason: event is JoinFailed ? event.detail : null,
    );
    for (final archive in StandardJoin.downloads) {
      expect(
        packageFolder(rig.game, archive.id, archive.version).existsSync(),
        isTrue,
      );
    }
  });
}

/// The label of a join's journal in what the launcher says about it.
const _journalLabel = 'The journal an earlier join left in the game folder';

/// What the launcher says of a join's journal it cannot read just now, for
/// [cause]: it names no path, and neither the journal's file nor the join's
/// folder.
Matcher _unreadableJournal(JoinRig rig, String cause) => allOf(
  startsWith(
    '$_journalLabel could not be read just now, so nothing was changed. '
    '$cause',
  ),
  isNot(contains(rig.root.path)),
  isNot(contains('journal.json')),
  isNot(contains('launcher-join-')),
);

/// The journal in the one join folder a killed install left in [game].
File _journalOf(Directory game) {
  final folders = joinFolders(game);
  expect(folders, hasLength(1));
  return File(
    p.join(managerStaging(game).path, folders.single, 'journal.json'),
  );
}

/// Leaves [journal] as a save of it leaves it when it stops between its two
/// moves: the journal moved aside under the writer's backup name, the new
/// one still in its temporary file, which is never put back, and no journal.
/// Returns the backup.
File _leaveOnlyBackup(File journal) {
  const token = '4242-1760520615100000';
  File('${journal.path}.$token.tmp').writeAsStringSync(
    journal.readAsStringSync().replaceFirst('"committing"', '"rollingBack"'),
  );
  return journal.renameSync('${journal.path}.$token.bak');
}

/// Every file and folder under [game]'s TopiaForge folder, by its path
/// there: each file with the SHA-256 of its bytes, or `unreadable`.
Map<String, String> _gameFiles(Directory game) {
  final folder = Directory(p.join(game.path, 'BepInEx', 'TopiaForge'));
  return {
    for (final entity in folder.listSync(recursive: true, followLinks: false))
      p.relative(entity.path, from: folder.path): entity is File
          ? _digestOf(entity)
          : 'folder',
  };
}

String _digestOf(File file) {
  try {
    return sha256.convert(file.readAsBytesSync()).toString();
  } on FileSystemException {
    return 'unreadable';
  }
}

String _launcherLog(JoinRig rig) {
  final log = File(p.join(rig.dataRoot, 'logs', 'launcher.log'));
  return log.existsSync() ? log.readAsStringSync() : '';
}

/// What `dart:io` does with no overrides in force.
final class _Plain extends IOOverrides {}

final _plain = _Plain();
