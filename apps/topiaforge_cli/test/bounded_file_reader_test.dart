import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:topiaforge/src/bounded_file_reader.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() => temp = Directory.systemTemp.createTempSync('bounded-file-test-'));
  tearDown(() => temp.deleteSync(recursive: true));

  test('reads a bounded strict-UTF8 JSON object', () {
    final file = File(p.join(temp.path, 'value.json'))
      ..writeAsStringSync('{"ok":true}');
    expect(readBoundedJsonObjectSync(file, maxBytes: 64)['ok'], isTrue);
  });

  test('rejects oversized and invalid UTF-8 inputs', () {
    final oversized = File(p.join(temp.path, 'large'))
      ..writeAsBytesSync(List.filled(65, 1));
    expect(
      () => readBoundedRegularFileSync(oversized, maxBytes: 64),
      throwsStateError,
    );
    final invalid = File(p.join(temp.path, 'invalid'))
      ..writeAsBytesSync([0xc3, 0x28]);
    expect(
      () => readBoundedTextFileSync(invalid, maxBytes: 64),
      throwsStateError,
    );
  });

  test('rejects symlink inputs', () {
    final target = File(p.join(temp.path, 'target'))
      ..writeAsStringSync('target');
    final link = Link(p.join(temp.path, 'link'))..createSync(target.path);
    expect(
      () => readBoundedRegularFileSync(File(link.path), maxBytes: 64),
      throwsStateError,
    );
  });

  test('detects same-size replacement between bounded passes', () {
    final file = File(p.join(temp.path, 'race'))..writeAsStringSync('first');
    expect(
      () => readBoundedTextFileSync(
        file,
        maxBytes: 64,
        afterFirstReadForTesting: () {
          file.writeAsStringSync('other', flush: true);
        },
      ),
      throwsStateError,
    );
  });

  test('bounds directory entry enumeration', () {
    File(p.join(temp.path, 'a')).writeAsStringSync('a');
    File(p.join(temp.path, 'b')).writeAsStringSync('b');
    expect(
      () => listBoundedDirectorySync(temp, maxEntries: 1),
      throwsStateError,
    );
  });

  test('reads only a bounded strict-UTF8 file tail', () {
    final file = File(p.join(temp.path, 'log'))
      ..writeAsStringSync('${'x' * 4096}\nolder\nnewest\n');
    final tail = readBoundedTextFileTailSync(file, maxBytes: 32);
    expect(utf8.encode(tail).length, lessThanOrEqualTo(32));
    expect(tail, endsWith('newest\n'));

    final splitCharacter = File(p.join(temp.path, 'utf8-tail'))
      ..writeAsStringSync('${'x' * 31}🙂tail');
    expect(readBoundedTextFileTailSync(splitCharacter, maxBytes: 6), 'tail');
  });

  test('surfaces malformed bounded tail and missing-file failures', () {
    final invalid = File(p.join(temp.path, 'invalid-tail'))
      ..writeAsBytesSync([0xff, 0xfe]);
    expect(
      () => readBoundedTextFileTailSync(invalid, maxBytes: 64),
      throwsStateError,
    );
    expect(
      () => readBoundedTextFileTailSync(
        File(p.join(temp.path, 'missing')),
        maxBytes: 64,
      ),
      throwsStateError,
    );
  });
}
