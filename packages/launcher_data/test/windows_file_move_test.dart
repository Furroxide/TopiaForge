@TestOn('windows')
library;

import 'dart:io';

import 'package:launcher_data/src/windows_file_move.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The move that puts a backup back on Windows refuses to replace anything
/// already at the target name, so a file that comes back between a look and
/// the move is never lost.
void main() {
  late Directory root;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-move-')
          .resolveSymbolicLinksSync(),
    );
  });
  tearDown(() => root.deleteSync(recursive: true));

  File at(Directory folder, String name, [String? content]) {
    final file = File(p.join(folder.path, name));
    if (content != null) file.writeAsStringSync(content);
    return file;
  }

  test('moves a file to a free name', () {
    final from = at(root, 'settings.json.4242-1.bak', 'saved');
    final to = at(root, 'settings.json');
    expect(moveFileWithoutReplacingOnWindows(from.path, to.path), isTrue);
    expect(from.existsSync(), isFalse);
    expect(to.readAsStringSync(), 'saved');
  });

  test('never replaces what is at the target name', () {
    final from = at(root, 'settings.json.4242-1.bak', 'older');
    final to = at(root, 'settings.json', 'came back');
    final folder = Directory(p.join(root.path, 'state.json'))..createSync();
    expect(moveFileWithoutReplacingOnWindows(from.path, to.path), isFalse);
    expect(moveFileWithoutReplacingOnWindows(from.path, folder.path), isFalse);
    expect(from.readAsStringSync(), 'older');
    expect(to.readAsStringSync(), 'came back');
    expect(folder.existsSync(), isTrue);
  });

  test('fails for a file that is gone', () {
    final to = at(root, 'settings.json');
    expect(
      moveFileWithoutReplacingOnWindows(
        p.join(root.path, 'settings.json.4242-1.bak'),
        to.path,
      ),
      isFalse,
    );
    expect(to.existsSync(), isFalse);
  });

  test('moves within a folder whose path is longer than MAX_PATH', () {
    var folder = root;
    while (folder.path.length < 300) {
      folder = Directory(p.join(folder.path, 'a' * 40));
    }
    folder.createSync(recursive: true);
    final from = at(folder, 'profiles.json.4242-1.bak', 'saved');
    final to = at(folder, 'profiles.json');
    expect(to.path.length, greaterThan(260));
    expect(moveFileWithoutReplacingOnWindows(from.path, to.path), isTrue);
    expect(to.readAsStringSync(), 'saved');
  });
}
