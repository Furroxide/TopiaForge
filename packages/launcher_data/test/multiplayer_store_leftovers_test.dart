import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// What interrupted writes leave beside the store's files: backups are put
/// back only under the store's own names, the newest one even within a
/// second, and never taken for missing while the folder cannot be searched;
/// stale temporary files are swept without touching a write in progress.
void main() {
  final now = DateTime.utc(2026, 10, 15, 12);
  late Directory root;
  late String folder;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-mp-leftovers-');
    folder = p.join(root.resolveSymbolicLinksSync(), 'multiplayer');
    Directory(folder).createSync(recursive: true);
  });
  tearDown(() => root.deleteSync(recursive: true));

  MultiplayerStore store() => MultiplayerStore(folder, clock: () => now);
  File inFolder(String name) => File(p.join(folder, name));
  List<String> names() => Directory(
    folder,
  ).listSync().map((entity) => p.basename(entity.path)).toList()..sort();

  /// A stored settings file named [name], holding [displayName].
  File settingsAs(String name, String displayName, DateTime modified) =>
      inFolder(name)
        ..writeAsStringSync(
          jsonEncode(MultiplayerSettings(displayName: displayName).toJson()),
        )
        ..setLastModifiedSync(modified);

  Matcher unreadable() => throwsA(
    isA<MultiplayerStoreException>().having(
      (error) => error.unreadable,
      'unreadable',
      isTrue,
    ),
  );

  group('a backup', () {
    test('is put back or dropped only under the store\'s own names', () async {
      final foreign = [
        'settings.json.bak',
        'settings.json.mine.bak',
        'settings.json.4242-1.bak',
        'settings.json.a-1-1.bak',
        'settings.json.4242-1-1.bak.old',
      ];
      for (final name in foreign) {
        settingsAs(name, 'Mine', DateTime(2026, 10, 9));
      }
      // Alone, none of them stands in for the file.
      final alone = await store().loadSettings();
      expect(alone.settings.toJson(), MultiplayerSettings().toJson());
      expect(inFolder(MultiplayerStore.settingsFileName).existsSync(), isFalse);

      // Beside the store's own, that one is put back and the others stay.
      settingsAs('settings.json.4242-1-1.bak', 'Sam', DateTime(2026, 10, 1));
      final load = await store().loadSettings();
      expect(load.settings.displayName, 'Sam');
      expect(names(), [...foreign, MultiplayerStore.settingsFileName]..sort());
    });

    test('of two from one second, the later write\'s is put back', () async {
      // Windows reports whole seconds here, so both read as one time. The
      // later write moved the later version aside.
      final second = DateTime(2026, 10, 15, 9, 30, 15);
      settingsAs(
        'settings.json.4242-1760520615100000-7.bak',
        'Earlier',
        second,
      );
      settingsAs('settings.json.4242-1760520615900000-3.bak', 'Later', second);
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.displayName, 'Later');
      expect(names(), [MultiplayerStore.settingsFileName]);
    });

    test('is never taken for missing while the folder cannot be searched for '
        'one', () async {
      final chosen = MultiplayerSettings(
        displayName: 'Sam',
        lanDiscoveryEnabled: false,
      );
      inFolder(
        'settings.json.4242-1-1.bak',
      ).writeAsStringSync(jsonEncode(chosen.toJson()));
      final shared = store();
      final real = Directory(folder);
      await IOOverrides.runZoned(() async {
        final load = await shared.loadSettings();
        expect(load.recovery, MultiplayerStoreRecovery.unreadable);
        expect(
          load.settings.toJson(),
          MultiplayerStore.unreadableSettings.toJson(),
        );
        await expectLater(
          shared.updateSettings((settings) => settings.withUpnp(true)),
          unreadable(),
        );
        await expectLater(shared.loadKnownHosts(), unreadable());
      }, createDirectory: (path) => _Unlistable(real, path, folder));
      expect(inFolder(MultiplayerStore.settingsFileName).existsSync(), isFalse);

      // Searchable again, the backup is put back and nothing was lost.
      final load = await shared.loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), chosen.toJson());
      expect(names(), [MultiplayerStore.settingsFileName]);
    });

    test('is not looked for where no folder can be', () async {
      final blocker = File(p.join(root.path, 'blocker'))..writeAsStringSync('');
      // A file in the folder's place, and a file in place of its parent.
      for (final path in [blocker.path, p.join(blocker.path, 'multiplayer')]) {
        final blocked = MultiplayerStore(path);
        final load = await blocked.loadSettings();
        expect(load.recovery, MultiplayerStoreRecovery.none, reason: path);
        expect(load.settings.toJson(), MultiplayerSettings().toJson());
        await expectLater(
          blocked.saveSettings(MultiplayerSettings()),
          throwsA(
            isA<MultiplayerStoreException>().having(
              (error) => error.unreadable,
              'unreadable',
              isFalse,
            ),
          ),
        );
      }
    });
  });

  group('a temporary file', () {
    final stale = now.subtract(MultiplayerStore.staleTemporaryAge);
    File temporary(String name, DateTime modified) => inFolder(name)
      ..writeAsStringSync('{"unfinished": ')
      ..setLastModifiedSync(modified);

    test('is swept on a store\'s first use once it is the store\'s own and '
        'an hour old', () async {
      temporary('settings.json.4242-1-1.tmp', stale);
      temporary('known-hosts.json.4242-2-2.tmp', stale);
      final kept = [
        // A write elsewhere may still be using a recent one.
        temporary(
          'settings.json.4242-3-3.tmp',
          stale.add(const Duration(minutes: 1)),
        ),
        temporary('settings.json.tmp', stale),
        temporary('settings.json.mine.tmp', stale),
        temporary('settings.json.4242-4.tmp', stale),
        temporary('profiles.json.4242-5-5.tmp', stale),
        temporary('settings.json.4242-6-6.tmp.keep', stale),
      ].map((file) => p.basename(file.path));
      Directory(p.join(folder, 'settings.json.4242-7-7.tmp')).createSync();
      final shared = store();
      await shared.loadSettings();
      expect(names(), [...kept, 'settings.json.4242-7-7.tmp']..sort());

      // Once per store: one that turns stale later waits for the next store.
      temporary('settings.json.4242-8-8.tmp', stale);
      await shared.loadSettings();
      expect(inFolder('settings.json.4242-8-8.tmp').existsSync(), isTrue);
      await store().loadSettings();
      expect(inFolder('settings.json.4242-8-8.tmp').existsSync(), isFalse);
    });

    test('is never swept through a link named like one', () async {
      final outside = File(p.join(root.path, 'outside.json'))
        ..writeAsStringSync('{}')
        ..setLastModifiedSync(stale);
      final link = Link(p.join(folder, 'settings.json.4242-1-1.tmp'));
      try {
        link.createSync(outside.path);
      } on FileSystemException {
        markTestSkipped('This account cannot make a link to a file.');
        return;
      }
      await store().loadSettings();
      expect(link.existsSync(), isTrue);
      expect(outside.readAsStringSync(), '{}');
    });

    test('is never swept while a write in progress uses it', () async {
      final entered = Completer<File>();
      final gate = Completer<void>();
      final writing = IOOverrides.runZoned(
        () => store().saveSettings(MultiplayerSettings(displayName: 'Sam')),
        createFile: (path) => path.endsWith('.tmp')
            ? _GatedFile(path, entered, gate.future)
            : _realFile(path),
      );
      final inProgress = await entered.future;
      // By its age alone, a sweep would take it.
      inProgress.setLastModifiedSync(now.subtract(const Duration(days: 1)));
      await store().loadSettings();
      expect(inProgress.existsSync(), isTrue);
      gate.complete();
      expect((await writing).displayName, 'Sam');
      expect((await store().loadSettings()).settings.displayName, 'Sam');
      expect(names(), [MultiplayerStore.settingsFileName]);
    });
  });
}

/// The store's folder as one that cannot be listed just now, as when its
/// permissions deny it, while everything else about it works.
final class _Unlistable implements Directory {
  _Unlistable(this._real, this.path, String folder) {
    if (path != folder) throw StateError('Only the store\'s folder is made.');
  }

  final Directory _real;

  @override
  final String path;

  @override
  Stream<FileSystemEntity> list({
    bool recursive = false,
    bool followLinks = true,
  }) => Stream.error(
    FileSystemException(
      'Directory listing failed',
      path,
      const OSError('Access is denied.', 5),
    ),
  );

  @override
  Future<Directory> create({bool recursive = false}) =>
      _real.create(recursive: recursive);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// A temporary file whose write pauses once its bytes are on disk, until
/// [_gate] opens.
final class _GatedFile implements File {
  _GatedFile(this.path, this._entered, this._gate);

  @override
  final String path;
  final Completer<File> _entered;
  final Future<void> _gate;

  @override
  Future<File> writeAsBytes(
    List<int> bytes, {
    FileMode mode = FileMode.write,
    bool flush = false,
  }) async {
    final real = _realFile(path);
    await real.writeAsBytes(bytes, mode: mode, flush: flush);
    if (!_entered.isCompleted) _entered.complete(real);
    await _gate;
    return this;
  }

  @override
  Future<File> rename(String newPath) => _realFile(path).rename(newPath);

  @override
  Future<FileSystemEntity> delete({bool recursive = false}) =>
      _realFile(path).delete(recursive: recursive);

  @override
  Future<FileStat> stat() => _realFile(path).stat();

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A file as `dart:io` makes one, whatever overrides are in force.
File _realFile(String path) =>
    IOOverrides.runWithIOOverrides(() => File(path), _plain);
