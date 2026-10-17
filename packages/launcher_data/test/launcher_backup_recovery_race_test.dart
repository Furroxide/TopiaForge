import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The desktop launcher and the command line share the launcher's data
/// folder, so a file can come back while one of them looks for its backups:
/// a save in the other process ends, or the other process puts the same
/// backup back first. A file that is there is read as it is, and no backup
/// is moved over it.
void main() {
  late Directory root;
  late Directory dataRoot;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-backup-race-')
          .resolveSymbolicLinksSync(),
    );
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
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
  File settings() => inData('settings.json');
  Map<String, Object?> saved() =>
      jsonDecode(settings().readAsStringSync()) as Map<String, Object?>;
  File backup(String token, Map<String, Object?> content, int day) =>
      inData('settings.json.$token.bak')
        ..writeAsStringSync(jsonEncode(content))
        ..setLastModifiedSync(DateTime(2026, 10, day));

  /// Runs [body] while the other process does [action] right after this one
  /// next looks at the settings once it has listed the data folder. That
  /// look still finds what was there before [action].
  Future<void> actingRightAfterTheLook(
    void Function() action,
    Future<void> Function() body,
  ) {
    var listed = false;
    var acted = false;
    return IOOverrides.runZoned(
      body,
      createDirectory: (path) => p.equals(path, dataRoot.path)
          ? _Listing(path, beforeListing: () => listed = true)
          : _realDirectory(path),
      fseGetTypeSync: (path, followLinks) {
        final type = IOOverrides.runWithIOOverrides(
          () => FileSystemEntity.typeSync(path, followLinks: followLinks),
          _plain,
        );
        if (listed && !acted && p.equals(path, settings().path)) {
          acted = true;
          IOOverrides.runWithIOOverrides(action, _plain);
        }
        return type;
      },
    );
  }

  test('a save the other process ends while the folder is looked at is '
      'read, not refused', () async {
    final repo = repository();
    // Missing when first looked at, and there once the folder lists.
    await IOOverrides.runZoned(
      () => repo.setDeveloperMode(true),
      createDirectory: (path) => p.equals(path, dataRoot.path)
          ? _Listing(
              path,
              beforeListing: () => settings().writeAsStringSync(
                jsonEncode({'selectedProfileId': 'sam-build'}),
              ),
            )
          : _realDirectory(path),
    );
    expect(saved(), {'selectedProfileId': 'sam-build', 'developerMode': true});
  });

  test('an older backup is never moved over the one the other process put '
      'back', () async {
    final repo = repository();
    final older = backup('4242-1759312800000000', {
      'selectedProfileId': 'older',
    }, 1);
    final newest = backup('4242-1759485600000000', {
      'selectedProfileId': 'sam-build',
    }, 3);
    await actingRightAfterTheLook(
      () => newest.renameSync(settings().path),
      () => repo.setDeveloperMode(true),
    );
    expect(saved(), {'selectedProfileId': 'sam-build', 'developerMode': true});
    expect(older.existsSync(), isTrue);
  });

  test(
    'a save the other process ends right after the last look is never '
    'replaced',
    () async {
      // What a save in the other process leaves while it runs: the file
      // moved aside, and its new version not yet moved in.
      final repo = repository();
      final aside = backup('4242-1759312800000000', {
        'selectedProfileId': 'older',
      }, 1);
      await actingRightAfterTheLook(
        () => settings().writeAsStringSync(
          jsonEncode({'selectedProfileId': 'sam-build'}),
        ),
        () => repo.setDeveloperMode(true),
      );
      expect(saved(), {
        'selectedProfileId': 'sam-build',
        'developerMode': true,
      });
      // The other process's save drops the backup it made once it is done.
      expect(aside.existsSync(), isTrue);
    },
    skip: Platform.isWindows
        ? false
        : 'rename(2) cannot refuse to replace, and a save there never '
              'leaves the file missing.',
  );
}

/// A folder listed as it really is, after [_beforeListing] runs once.
final class _Listing implements Directory {
  _Listing(this.path, {void Function()? beforeListing})
    : _beforeListing = beforeListing;

  @override
  final String path;
  void Function()? _beforeListing;

  Directory get _real => _realDirectory(path);

  void _beforeFirstListing() {
    final before = _beforeListing;
    _beforeListing = null;
    if (before != null) IOOverrides.runWithIOOverrides(before, _plain);
  }

  @override
  Stream<FileSystemEntity> list({
    bool recursive = false,
    bool followLinks = true,
  }) async* {
    _beforeFirstListing();
    yield* _real.list(recursive: recursive, followLinks: followLinks);
  }

  @override
  List<FileSystemEntity> listSync({
    bool recursive = false,
    bool followLinks = true,
  }) {
    _beforeFirstListing();
    return _real.listSync(recursive: recursive, followLinks: followLinks);
  }

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
