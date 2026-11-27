import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';

// The published schemas check versions with one SemVer 2.0.0 pattern. Editors
// and other validators run it with backtracking engines on versions someone
// else wrote, so it has to accept exactly SemVer and refuse any input in linear
// time.
void main() {
  const schemaNames = [
    'topiaforge.mod.schema.json',
    'topiaforge.mod.v6.schema.json',
    'topiaforge.registry-entry.schema.json',
    'topiaforge.registry-index.schema.json',
  ];
  final patternsBySchema = {
    for (final name in schemaNames) name: _versionPatterns(_readSchema(name)),
  };

  test('every schema checks versions with the same pattern', () {
    for (final entry in patternsBySchema.entries) {
      expect(entry.value, isNotEmpty, reason: entry.key);
    }
    expect(
      patternsBySchema.values.expand((patterns) => patterns).toSet(),
      hasLength(1),
    );
  });

  for (final MapEntry(key: name, value: patterns) in patternsBySchema.entries) {
    for (final pattern in patterns) {
      final version = RegExp(pattern);

      test('$name accepts exactly SemVer 2.0.0 versions', () {
        for (final accepted in _accepted) {
          expect(version.hasMatch(accepted), isTrue, reason: accepted);
        }
        for (final refused in _refused) {
          expect(version.hasMatch(refused), isFalse, reason: refused);
        }
      });

      test('$name refuses hostile versions in linear time', () {
        for (final hostile in _hostile) {
          final watch = Stopwatch()..start();
          expect(version.hasMatch(hostile), isFalse);
          expect(
            watch.elapsedMilliseconds,
            lessThan(100),
            reason: 'refusing a ${hostile.length}-character version',
          );
        }
      });
    }
  }
}

const _accepted = [
  '0.0.0',
  '1.2.3',
  '10.20.30',
  '1.0.0-0',
  '1.0.0-rc.1',
  '1.0.0-0a',
  '1.0.0-01a',
  '1.0.0--',
  '1.0.0-a-',
  '1.0.0-x.7.z.92',
  '1.0.0-alpha+001',
  '1.0.0+20130313144700',
  '1.0.0-beta+exp.sha.5114f85',
];

const _refused = [
  '1',
  '1.2',
  '01.2.3',
  '1.02.3',
  '1.2.03',
  '1.2.3-',
  '1.2.3-01',
  '1.2.3-rc.01',
  '1.2.3-rc..1',
  '1.2.3-rc.',
  '1.2.3-rc_1',
  '1.2.3+',
  '1.2.3+a..b',
  '1.2.3-rélease',
  ' 1.2.3',
  '1.2.3 ',
];

// A run of hyphens or alternating digits and hyphens that ends in an empty
// identifier: a pattern that can split an identifier at more than one place
// tries every split before refusing it.
final _hostile = [
  '0.0.0-${'-' * 58}.',
  '0.0.0-${'----.' * 11}',
  '0.0.0-${'-' * 4096}.',
  '0.0.0-${'1-' * 2048}.',
];

Set<String> _versionPatterns(Object? node) {
  final found = <String>{};
  void walk(Object? value) {
    if (value is Map) {
      for (final MapEntry(:key, :value) in value.entries) {
        if (key == 'pattern' &&
            value is String &&
            value.startsWith(r'^(0|[1-9][0-9]*)\.')) {
          found.add(value);
        }
        walk(value);
      }
    } else if (value is List) {
      value.forEach(walk);
    }
  }

  walk(node);
  return found;
}

Object? _readSchema(String name) {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
  return jsonDecode(File('${directory.path}/schemas/$name').readAsStringSync());
}
