import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// What an interrupted save leaves beside the launcher's own files in its
/// data folder, and what reading them does with it.
///
/// A save that cannot replace a file in one move first moves the old one
/// aside as `<name>.<process id>-<microseconds>.bak`; stopped between its two
/// moves, it leaves that backup and no file. Reading puts the newest backup
/// back, under the launcher's own names only and the later write's within one
/// second. While that backup cannot be put back, or the folder cannot be
/// listed or searched, the file is unreadable: nothing is written over it, no
/// older backup stands in for it, and nothing is deleted.
void main() {
  late Directory root;
  late Directory dataRoot;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-backups-')
          .resolveSymbolicLinksSync(),
    );
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    // The official registry stays off, so no test reaches the network.
    File(p.join(dataRoot.path, 'package_sources.json')).writeAsStringSync(
      jsonEncode({
        'formatVersion': 2,
        'sources': [_official.toJson()],
      }),
    );
  });
  tearDown(() => root.deleteSync(recursive: true));

  LocalLauncherRepository repository() {
    final created = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: root.path,
      knownGamePath: p.join(root.path, 'missing-game'),
    );
    addTearDown(created.dispose);
    return created;
  }

  File inData(String name) => File(p.join(dataRoot.path, name));

  /// The names in the data folder that start with [prefix], sorted.
  List<String> named(String prefix) => [
    for (final entity in dataRoot.listSync())
      if (p.basename(entity.path).startsWith(prefix)) p.basename(entity.path),
  ]..sort();

  /// What a save stopped between its two moves leaves: [file] moved aside
  /// under the launcher's own name for it, and no [file].
  File interrupt(File file, String token) =>
      file.renameSync('${file.path}.$token.bak');

  File settingsAs(String name, Map<String, Object?> settings, DateTime at) =>
      inData(name)
        ..writeAsStringSync(jsonEncode(settings))
        ..setLastModifiedSync(at);

  Matcher unreadable(String label) => throwsA(
    isA<FileSystemException>().having(
      (error) => error.toString(),
      'message',
      allOf(
        startsWith(
          '$label could not be read just now, so nothing was changed.',
        ),
        isNot(contains(root.path)),
      ),
    ),
  );

  final sam = LauncherProfile.defaultProfile().copyWith(
    id: 'sam-build',
    name: "Sam's build",
  );
  final alex = LauncherProfile.defaultProfile().copyWith(
    id: 'alex-build',
    name: "Alex's build",
  );
  PackageSource samSource() => PackageSource(
    id: 'sam.packages',
    name: "Sam's packages",
    url: Uri.directory(p.join(root.path, 'sam-packages')).toString(),
  );

  group('a backup an interrupted save left', () {
    test(
      "is put back or dropped only under the launcher's own names",
      () async {
        final foreign = [
          'settings.json.bak',
          'settings.json.mine.bak',
          'settings.json.4242.bak',
          'settings.json.a-1.bak',
          'settings.json.4242-1-1.bak',
          'settings.json.4242-1.bak.old',
          // As Windows names a copy made beside one of the launcher's own.
          'settings.json.4242-1760520615100000 - Copy.bak',
        ];
        for (final name in foreign) {
          settingsAs(name, {
            'developerMode': true,
            'selectedProfileId': 'mine',
          }, DateTime(2026, 10, 9));
        }
        final kept = {
          for (final name in foreign) name: inData(name).readAsBytesSync(),
        };

        // Alone, none of them stands in for the settings.
        final alone = await repository().loadSnapshot();
        expect(alone.developerMode, isFalse);
        expect(named('settings.json'), [...foreign]..sort());

        // Beside the launcher's own, that one is put back and the others stay.
        settingsAs('settings.json.4242-1759312800000000.bak', {
          'developerMode': true,
          'selectedProfileId': sam.id,
        }, DateTime(2026, 10, 1));
        final restored = await repository().loadSnapshot();
        expect(restored.developerMode, isTrue);
        expect(restored.selectedProfileId, sam.id);
        expect(named('settings.json'), [...foreign, 'settings.json']..sort());
        for (final name in foreign) {
          expect(inData(name).readAsBytesSync(), kept[name], reason: name);
        }
      },
    );

    test("of backups from one second, the later write's is put back", () async {
      // Windows reports modification times in whole seconds here, so all of
      // them read as one time. Each later write moved a later version aside.
      // One process names its backups in write order; several processes
      // name the latest write's neither first nor last.
      final second = DateTime(2026, 10, 15, 9, 30, 15);
      for (var round = 0; round < 4; round++) {
        final start = 1760520615000000 + round * 1000;
        final processes = round.isEven
            ? const [4242, 4242, 4242, 4242]
            : const [3000, 1000, 4000, 2000];
        for (var write = 0; write < 4; write++) {
          final token = '${processes[write] + round}-${start + write * 100}';
          settingsAs('settings.json.$token.bak', {
            'selectedProfileId': 'write-$write',
          }, second);
        }
        final snapshot = await repository().loadSnapshot();
        expect(snapshot.selectedProfileId, 'write-3', reason: 'round $round');
        expect(named('settings.json'), ['settings.json'], reason: '$round');
        inData('settings.json').deleteSync();
      }
    });

    test('held by another program leaves the profiles unreadable, never '
        'missing', () async {
      final repo = repository();
      await repo.loadSnapshot();
      await repo.saveProfiles([sam, alex], sam.id);
      final original = inData('profiles.json').readAsBytesSync();
      final backup = interrupt(
        inData('profiles.json'),
        '4242-1760520615100000',
      );
      final release = await holdInPlace(backup);
      if (release == null) return;

      await expectLater(repo.loadSnapshot(), unreadable('Launcher profiles'));
      await expectLater(
        repo.saveProfiles([LauncherProfile.defaultProfile()], 'default'),
        unreadable('Launcher profiles'),
      );
      expect(inData('profiles.json').existsSync(), isFalse);
      await release();
      expect(backup.readAsBytesSync(), original);

      // Free again, the backup is put back and nothing was lost.
      final snapshot = await repo.loadSnapshot();
      expect(snapshot.profiles.map((profile) => profile.id), [sam.id, alex.id]);
      expect(named('profiles.json'), ['profiles.json']);
    });

    test(
      'held by another program never lets an older backup stand in',
      () async {
        final repo = repository();
        await repo.loadSnapshot();
        final older = settingsAs('settings.json.4242-1759312800000000.bak', {
          'developerMode': false,
          'selectedProfileId': 'older',
        }, DateTime(2026, 10, 1));
        final newest = settingsAs('settings.json.4242-1759485600000000.bak', {
          'developerMode': true,
          'selectedProfileId': sam.id,
        }, DateTime(2026, 10, 3));
        final release = await holdInPlace(newest);
        if (release == null) return;

        await expectLater(repo.loadSnapshot(), unreadable('Launcher settings'));
        await expectLater(
          repo.setDeveloperMode(false),
          unreadable('Launcher settings'),
        );
        expect(inData('settings.json').existsSync(), isFalse);
        expect(older.existsSync(), isTrue);
        await release();

        final snapshot = await repo.loadSnapshot();
        expect(snapshot.developerMode, isTrue);
        expect(snapshot.selectedProfileId, sam.id);
        expect(named('settings.json'), ['settings.json']);
      },
    );

    test('held by another program leaves the package sources unreadable, and '
        'a save never writes over them', () async {
      final repo = repository();
      await repo.loadSnapshot();
      await repo.savePackageSources([_official, samSource()]);
      final original = inData('package_sources.json').readAsBytesSync();
      final backup = interrupt(
        inData('package_sources.json'),
        '4242-1760520615100000',
      );
      final release = await holdInPlace(backup);
      if (release == null) return;

      await expectLater(
        repo.savePackageSources([_official]),
        unreadable('Package sources'),
      );
      await expectLater(repo.loadSnapshot(), unreadable('Package sources'));
      expect(inData('package_sources.json').existsSync(), isFalse);
      await release();
      expect(backup.readAsBytesSync(), original);

      final snapshot = await repo.loadSnapshot();
      expect(
        snapshot.packageSources.map((source) => source.id),
        contains(samSource().id),
      );
      expect(named('package_sources.json'), ['package_sources.json']);
    });
  });

  group('a folder that cannot be listed or searched', () {
    test('never stands the default package sources in, nor contacts a '
        'source the player switched off', () async {
      final repo = repository();
      await repo.loadSnapshot();
      await repo.savePackageSources([_official, samSource()]);
      final install = _bareInstall(Directory(p.join(root.path, 'game')));
      // As `chmod 444` leaves the folder: its names list, but every file in
      // it looks missing. First the file itself, then only the backup a save
      // left of it.
      for (final onlyBackup in [false, true]) {
        var sources = inData('package_sources.json');
        if (onlyBackup) sources = interrupt(sources, '4242-1760520615100000');
        final release = await denySearching(dataRoot, sources);
        if (release == null) return;
        final requests = <Uri>[];
        Object? failure;
        await HttpOverrides.runZoned(() async {
          try {
            await repo.loadExactPackageCatalog(install);
          } on Object catch (error) {
            failure = error;
          }
        }, createHttpClient: (_) => _RefusingHttpClient(requests));
        expect(requests, isEmpty, reason: 'only backup: $onlyBackup');
        expect(
          () => throw failure ?? StateError('The catalog loaded.'),
          unreadable('Package sources'),
        );
        await release();

        // Searchable again, the player's sources hold and nothing was lost.
        await HttpOverrides.runZoned(
          () => repo.loadExactPackageCatalog(install),
          createHttpClient: (_) => _RefusingHttpClient(requests),
        );
        expect(requests, isEmpty);
        expect(named('package_sources.json'), ['package_sources.json']);
      }
    });

    test('that cannot be listed leaves a missing file unreadable', () async {
      final repo = repository();
      await repo.loadSnapshot();
      await repo.setDeveloperMode(true);
      final original = inData('settings.json').readAsBytesSync();
      final backup = interrupt(
        inData('settings.json'),
        '4242-1760520615100000',
      );
      // As permissions that deny listing the folder leave it.
      await IOOverrides.runZoned(
        () async => expectLater(
          repo.setDeveloperMode(false),
          unreadable('Launcher settings'),
        ),
        createDirectory: (path) => p.equals(path, dataRoot.path)
            ? _Unlistable(path)
            : _realDirectory(path),
      );
      expect(inData('settings.json').existsSync(), isFalse);
      expect(backup.readAsBytesSync(), original);

      // Listable again, the backup is put back and nothing was lost.
      expect((await repo.loadSnapshot()).developerMode, isTrue);
      expect(named('settings.json'), ['settings.json']);
    });

    test(
      'whose mode denies listing leaves a missing file unreadable',
      () async {
        final repo = repository();
        await repo.loadSnapshot();
        await repo.setDeveloperMode(true);
        final original = inData('settings.json').readAsBytesSync();
        final backup = interrupt(
          inData('settings.json'),
          '4242-1760520615100000',
        );
        // As `chmod 300` leaves it: written and searched, never listed.
        final release = await denyListing(dataRoot);
        if (release == null) return;
        await expectLater(
          repo.setDeveloperMode(false),
          unreadable('Launcher settings'),
        );
        await release();
        expect(inData('settings.json').existsSync(), isFalse);
        expect(backup.readAsBytesSync(), original);
        expect((await repo.loadSnapshot()).developerMode, isTrue);
        expect(named('settings.json'), ['settings.json']);
      },
    );
  });
}

/// The official registry, switched off.
const _official = PackageSource(
  id: ModRegistryFormat.officialSourceId,
  name: ModRegistryFormat.officialSourceName,
  url: ModRegistryFormat.officialRegistryUrl,
  enabled: false,
  builtIn: true,
);

/// A game folder with nothing installed in it.
GameInstall _bareInstall(Directory game) => GameInstall(
  path: game.path,
  executablePath: p.join(game.path, 'game.exe'),
  bepInExStatus: ComponentState.ready,
  loaderStatus: ComponentState.ready,
);

/// Records each request it is asked to make and makes none.
final class _RefusingHttpClient implements HttpClient {
  _RefusingHttpClient(this._requests);

  final List<Uri> _requests;

  @override
  Duration? connectionTimeout;

  @override
  bool autoUncompress = true;

  @override
  Future<HttpClientRequest> getUrl(Uri url) {
    _requests.add(url);
    return Future.error(const SocketException('Tests make no requests.'));
  }

  @override
  void close({bool force = false}) {}

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// A folder that cannot be listed just now, as when its permissions deny it,
/// while everything else about it works.
final class _Unlistable implements Directory {
  _Unlistable(this.path) : _real = _realDirectory(path);

  final Directory _real;

  @override
  final String path;

  FileSystemException get _denied => FileSystemException(
    'Directory listing failed',
    path,
    OSError('Access is denied.', Platform.isWindows ? 5 : 13),
  );

  @override
  Stream<FileSystemEntity> list({
    bool recursive = false,
    bool followLinks = true,
  }) => Stream.error(_denied);

  @override
  List<FileSystemEntity> listSync({
    bool recursive = false,
    bool followLinks = true,
  }) => throw _denied;

  @override
  Future<bool> exists() => _real.exists();

  @override
  bool existsSync() => _real.existsSync();

  @override
  Future<Directory> create({bool recursive = false}) =>
      _real.create(recursive: recursive);

  @override
  void createSync({bool recursive = false}) =>
      _real.createSync(recursive: recursive);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A folder as `dart:io` makes one, whatever overrides are in force.
Directory _realDirectory(String path) =>
    IOOverrides.runWithIOOverrides(() => Directory(path), _plain);
