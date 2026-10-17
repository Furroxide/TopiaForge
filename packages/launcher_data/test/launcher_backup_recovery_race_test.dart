import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The desktop launcher and the command line share the launcher's data
/// folder, so a file can come back while one of them looks for its backups:
/// a save in the other process ends, or the other process puts the same
/// backup back first. A file that is there is read as it is, and no backup
/// is ever moved over it.
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
  Map<String, Object?> saved() =>
      jsonDecode(inData('settings.json').readAsStringSync())
          as Map<String, Object?>;

  /// Runs [body] with the data folder listed through [listing].
  Future<void> listedThrough(_Listing listing, Future<void> Function() body) =>
      IOOverrides.runZoned(
        body,
        createDirectory: (path) =>
            p.equals(path, dataRoot.path) ? listing : _realDirectory(path),
      );

  test('a save the other process ends while the folder is looked at is '
      'read, not refused', () async {
    final repo = repository();
    // Missing when first looked at, and there once the folder lists.
    final listing = _Listing(
      dataRoot.path,
      beforeListing: () => inData(
        'settings.json',
      ).writeAsStringSync(jsonEncode({'selectedProfileId': 'sam-build'})),
    );
    await listedThrough(listing, () => repo.setDeveloperMode(true));
    expect(saved(), {'selectedProfileId': 'sam-build', 'developerMode': true});
  });

  test('an older backup is never moved over the one the other process put '
      'back', () async {
    final repo = repository();
    File backup(String token, Map<String, Object?> settings, int day) =>
        inData('settings.json.$token.bak')
          ..writeAsStringSync(jsonEncode(settings))
          ..setLastModifiedSync(DateTime(2026, 10, day));
    final older = backup('4242-1759312800000000', {
      'selectedProfileId': 'older',
    }, 1);
    final newest = backup('4242-1759485600000000', {
      'selectedProfileId': 'sam-build',
    }, 3);
    final listing = _Listing(
      dataRoot.path,
      entity: (entity) => p.equals(entity.path, newest.path)
          ? _PutBackFirst(newest.path)
          : entity,
    );
    await listedThrough(listing, () => repo.setDeveloperMode(true));
    expect(saved(), {'selectedProfileId': 'sam-build', 'developerMode': true});
    expect(older.existsSync(), isTrue);
  });
}

/// A folder listed as it really is, after [_beforeListing] runs once, with
/// each entity passed through [_entity].
final class _Listing implements Directory {
  _Listing(
    this.path, {
    void Function()? beforeListing,
    FileSystemEntity Function(FileSystemEntity entity)? entity,
  }) : _beforeListing = beforeListing,
       _entity = entity;

  @override
  final String path;
  void Function()? _beforeListing;
  final FileSystemEntity Function(FileSystemEntity entity)? _entity;

  Directory get _real => _realDirectory(path);

  @override
  Stream<FileSystemEntity> list({
    bool recursive = false,
    bool followLinks = true,
  }) async* {
    final before = _beforeListing;
    _beforeListing = null;
    before?.call();
    await for (final entity in _real.list(
      recursive: recursive,
      followLinks: followLinks,
    )) {
      yield _entity?.call(entity) ?? entity;
    }
  }

  @override
  List<FileSystemEntity> listSync({
    bool recursive = false,
    bool followLinks = true,
  }) {
    final before = _beforeListing;
    _beforeListing = null;
    before?.call();
    return [
      for (final entity in _real.listSync(
        recursive: recursive,
        followLinks: followLinks,
      ))
        _entity?.call(entity) ?? entity,
    ];
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

/// A backup the other process puts back just before this one moves it, so
/// this move finds it gone.
final class _PutBackFirst implements File {
  _PutBackFirst(this.path);

  @override
  final String path;

  @override
  FileStat statSync() => _realFile(path).statSync();

  @override
  Future<File> rename(String newPath) async {
    await _realFile(path).rename(newPath);
    throw FileSystemException(
      'Cannot rename file',
      path,
      const OSError('No such file or directory', 2),
    );
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A folder as `dart:io` makes one, whatever overrides are in force.
Directory _realDirectory(String path) =>
    IOOverrides.runWithIOOverrides(() => Directory(path), _plain);

/// A file as `dart:io` makes one, whatever overrides are in force.
File _realFile(String path) =>
    IOOverrides.runWithIOOverrides(() => File(path), _plain);
