import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:robotopia/src/mod_registry_index_builder.dart';
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory previous;
  late Directory current;

  setUp(() {
    root = Directory.systemTemp.createTempSync('registry-history-test-');
    previous = Directory(p.join(root.path, 'previous'))..createSync();
    current = Directory(p.join(root.path, 'current'))..createSync();
    _write(previous, [_version('1.0.0', 'a')]);
  });

  tearDown(() => root.deleteSync(recursive: true));

  test('identical history is idempotent and newer versions may prepend', () {
    _write(current, [_version('1.1.0', 'b'), _version('1.0.0', 'a')]);
    expect(
      validateRegistryPublicationHistory(
        entriesDirectory: current.path,
        previousEntriesDirectory: previous.path,
      ),
      isEmpty,
    );
  });

  test('deletion, reorder, and changed immutable bytes are rejected', () {
    expect(
      validateRegistryPublicationHistory(
        entriesDirectory: current.path,
        previousEntriesDirectory: previous.path,
      ),
      isNotEmpty,
    );
    _write(current, [_version('1.0.0', 'changed')]);
    expect(
      validateRegistryPublicationHistory(
        entriesDirectory: current.path,
        previousEntriesDirectory: previous.path,
      ).single.message,
      contains('original source'),
    );
    _write(current, [_version('0.9.0', 'b'), _version('1.0.0', 'a')]);
    expect(
      validateRegistryPublicationHistory(
        entriesDirectory: current.path,
        previousEntriesDirectory: previous.path,
      ),
      isNotEmpty,
    );
  });
}

Map<String, Object?> _version(String version, String marker) => {
  'version': version,
  'downloadUrl': 'https://example.invalid/$version.robotopiamod',
  'packageSha256': marker.padRight(64, '0'),
  'futureVersionField': {'preserved': marker},
};

void _write(Directory directory, List<Map<String, Object?>> versions) {
  File(p.join(directory.path, 'author.mod.json')).writeAsStringSync(
    jsonEncode({'formatVersion': 1, 'id': 'author.mod', 'versions': versions}),
  );
}
