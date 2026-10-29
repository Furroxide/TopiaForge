import 'dart:io';

import 'package:launcher_data/src/atomic_file_writes.dart';
import 'package:launcher_data/src/atomic_files.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// What a save stopped part way leaves is known by the names the writer gives
/// it, and only those are ever swept: never a copy the player keeps, a link,
/// a folder, a leftover a save may still own, or another file's.
void main() {
  late Directory root;
  late File settings;
  final old = DateTime.now().subtract(const Duration(hours: 2));
  final recent = DateTime.now().subtract(const Duration(minutes: 5));

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-atomic-sweep-');
    settings = File(p.join(root.path, 'settings.json'))
      ..writeAsStringSync('{}');
  });
  tearDown(() => root.deleteSync(recursive: true));

  String token(int processId, DateTime at) =>
      '$processId-${at.microsecondsSinceEpoch}';

  File leftover(String name, {required DateTime modified}) =>
      File(p.join(root.path, name))
        ..writeAsStringSync('leftover')
        ..setLastModifiedSync(modified);

  test('the naming rule counts only `<name>.<token>.bak` and `.tmp`', () {
    for (final suffix in ['.bak', '.tmp']) {
      expect(
        atomicLeftoverWrittenAt(
          'settings.json.4242-1760520615100000$suffix',
          'settings.json',
          suffix,
        ),
        1760520615100000,
      );
      for (final entry in [
        'settings.json$suffix',
        'settings.json.mine$suffix',
        'settings.json.4242$suffix',
        'settings.json.a-1$suffix',
        'settings.json.4242-1-1$suffix',
        'settings.json.4242-1760520615100000 - Copy$suffix',
        'settings.json.4242-1760520615100000${suffix}x',
        'other.json.4242-1760520615100000$suffix',
        'xsettings.json.4242-1760520615100000$suffix',
      ]) {
        expect(
          atomicLeftoverWrittenAt(entry, 'settings.json', suffix),
          isNull,
          reason: entry,
        );
      }
    }
  });

  test('sweeps only its own old temporary files', () {
    final stale = leftover(
      'settings.json.${token(7777, old)}.tmp',
      modified: old,
    );
    final kept = [
      // The player's own names, and another file's leftover.
      leftover('settings.json.tmp', modified: old),
      leftover('settings.json.mine.tmp', modified: old),
      leftover('settings.json.4242.tmp', modified: old),
      leftover('other.json.${token(7777, old)}.tmp', modified: old),
      // A save that began recently, or a leftover that changed recently,
      // may still belong to a save in another process.
      leftover('settings.json.${token(1111, recent)}.tmp', modified: old),
      leftover('settings.json.${token(2222, old)}.tmp', modified: recent),
      // A backup is put back, never swept, for a file with player state.
      leftover('settings.json.${token(3333, old)}.bak', modified: old),
      leftover('settings.json.bak', modified: old),
    ];
    final folder = Directory(
      p.join(root.path, 'settings.json.${token(5555, old)}.tmp'),
    )..createSync();

    sweepAtomicLeftovers(settings);

    expect(stale.existsSync(), isFalse);
    for (final file in kept) {
      expect(file.existsSync(), isTrue, reason: p.basename(file.path));
    }
    expect(folder.existsSync(), isTrue);
    expect(settings.readAsStringSync(), '{}');
  });

  test('sweeps old backups only where asked, for a file without player '
      'state', () {
    final backup = leftover(
      'settings.json.${token(3333, old)}.bak',
      modified: old,
    );
    final recentBackup = leftover(
      'settings.json.${token(3333, recent)}.bak',
      modified: recent,
    );
    final mine = leftover('settings.json.bak', modified: old);

    sweepAtomicLeftovers(settings, backups: true);

    expect(backup.existsSync(), isFalse);
    expect(recentBackup.existsSync(), isTrue);
    expect(mine.existsSync(), isTrue);
  });

  test('never follows or deletes a link named like a leftover', () {
    final outside = Directory.systemTemp.createTempSync('tf-atomic-outside-');
    addTearDown(() => outside.deleteSync(recursive: true));
    final target = File(p.join(outside.path, 'kept.txt'))
      ..writeAsStringSync('kept')
      ..setLastModifiedSync(old);
    final linkPath = p.join(root.path, 'settings.json.${token(6666, old)}.tmp');
    // A link to a file where the system allows one; a Windows account
    // without that right gets a junction to the folder instead.
    try {
      Link(linkPath).createSync(target.path);
    } on FileSystemException {
      Link(linkPath).createSync(outside.path);
    }

    sweepAtomicLeftovers(settings, backups: true);

    expect(
      FileSystemEntity.typeSync(linkPath, followLinks: false),
      FileSystemEntityType.link,
    );
    expect(target.readAsStringSync(), 'kept');
  });

  test('a save sweeps the old temporary files of its file before it '
      'writes', () async {
    final stale = leftover(
      'settings.json.${token(7777, old)}.tmp',
      modified: old,
    );
    final mine = leftover('settings.json.tmp', modified: old);

    await writeFileBytesAtomic(settings, [0x7B, 0x7D, 0x0A]);
    expect(stale.existsSync(), isFalse);
    expect(mine.existsSync(), isTrue);

    final again = leftover(
      'settings.json.${token(8888, old)}.tmp',
      modified: old,
    );
    writeFileBytesAtomicSync(settings, [0x7B, 0x7D]);
    expect(again.existsSync(), isFalse);
    expect(settings.readAsStringSync(), '{}');
    expect(root.listSync().map((entity) => p.basename(entity.path)).toSet(), {
      'settings.json',
      'settings.json.tmp',
    });
  });

  test('a save replaces the file in one move, so the file is never missing '
      'while it saves', () async {
    // Another process that finds the file missing puts back the newest
    // backup, and on Linux and macOS that move cannot refuse to replace a
    // file that came back, so a save there must never leave it missing.
    var looks = 0;
    var missing = 0;
    void look() {
      looks++;
      if (!IOOverrides.runWithIOOverrides(settings.existsSync, _plain)) {
        missing++;
      }
    }

    await IOOverrides.runZoned(
      () async {
        await writeFileBytesAtomic(settings, [0x5B, 0x5D]);
        writeFileBytesAtomicSync(settings, [0x7B, 0x7D]);
      },
      createFile: (path) =>
          path.endsWith('.tmp') ? _BeforeRename(path, look) : _realFile(path),
    );

    // Each save looked once, just before its temporary file moved in.
    expect(looks, 2);
    expect(missing, 0);
    expect(settings.readAsStringSync(), '{}');
  });

  test(
    'a leftover another program holds waits for a later sweep, and the save '
    'goes ahead',
    () async {
      final held = leftover(
        'settings.json.${token(7777, old)}.tmp',
        modified: old,
      );
      final release = await holdInPlace(held);
      if (release == null) return;
      // The fixture must hold a real readable file without delete sharing.
      expect(held.readAsStringSync(), isNotEmpty);
      expect(
        () => held.renameSync('${held.path}.moved'),
        throwsA(isA<FileSystemException>()),
      );
      expect(() => held.deleteSync(), throwsA(isA<FileSystemException>()));

      await writeFileBytesAtomic(settings, [0x5B, 0x5D]);
      expect(settings.readAsStringSync(), '[]');
      expect(held.existsSync(), isTrue);

      await release();
      await release(); // Explicit release and teardown share one child owner.
      writeFileBytesAtomicSync(settings, [0x7B, 0x7D]);
      expect(held.existsSync(), isFalse);
    },
    skip: Platform.isWindows
        ? false
        : 'Holding a file in place elsewhere freezes its whole folder, so no '
              'save could be written there either.',
  );

  test('never sweeps the temporary file a save in this isolate is '
      'writing, however old it looks', () async {
    // The sweep runs, with no age limit at all, just as the save is about to
    // move its temporary file in.
    var swept = false;
    await IOOverrides.runZoned(
      () => writeFileBytesAtomic(settings, [0x5B, 0x5D]),
      createFile: (path) => path.endsWith('.tmp')
          ? _BeforeRename(path, () {
              swept = true;
              IOOverrides.runWithIOOverrides(
                () => sweepAtomicLeftovers(settings, age: Duration.zero),
                _plain,
              );
            })
          : _realFile(path),
    );
    expect(swept, isTrue);
    expect(settings.readAsStringSync(), '[]');
  });
}

/// A temporary file whose moves run [_beforeRename] first.
final class _BeforeRename implements File {
  _BeforeRename(this.path, this._beforeRename);

  @override
  final String path;
  final void Function() _beforeRename;

  File get _real => _realFile(path);

  @override
  Future<File> writeAsBytes(
    List<int> bytes, {
    FileMode mode = FileMode.write,
    bool flush = false,
  }) => _real.writeAsBytes(bytes, mode: mode, flush: flush);

  @override
  void writeAsBytesSync(
    List<int> bytes, {
    FileMode mode = FileMode.write,
    bool flush = false,
  }) => _real.writeAsBytesSync(bytes, mode: mode, flush: flush);

  @override
  Future<File> rename(String newPath) {
    _beforeRename();
    return _real.rename(newPath);
  }

  @override
  File renameSync(String newPath) {
    _beforeRename();
    return _real.renameSync(newPath);
  }

  @override
  Future<bool> exists() => _real.exists();

  @override
  bool existsSync() => _real.existsSync();

  @override
  void deleteSync({bool recursive = false}) =>
      _real.deleteSync(recursive: recursive);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A file as `dart:io` makes one, whatever overrides are in force.
File _realFile(String path) =>
    IOOverrides.runWithIOOverrides(() => File(path), _plain);
