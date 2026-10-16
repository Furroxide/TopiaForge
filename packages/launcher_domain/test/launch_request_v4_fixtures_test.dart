import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// Every V4 wire-profile fixture reads through the staged-request reader that
/// request V5 added exactly as through the V4 reader: the same verdict and the
/// same normalized bytes. The game's C# tests replay them the same way.
void main() {
  final cases = _profileCases();

  test('the V4 wire-profile corpus is complete', () {
    expect(cases, hasLength(26));
    expect(
      cases.where((item) => _expected(item)['outcome'] == 'accept'),
      hasLength(2),
    );
  });

  for (final item in cases) {
    test('${item['id']} reads as the V4 reader reads it', () {
      final text = (item['wireJson'] as String?) ?? jsonEncode(item['payload']);
      String? v4;
      try {
        v4 = LaunchTransport.writeProfile(LaunchTransport.readProfile(text));
      } on FormatException {
        v4 = null;
      }
      String? staged;
      try {
        final request = StagedLaunchRequest.decode(text);
        expect(request, isA<StagedV4LaunchRequest>());
        expect(request.session, isNull);
        staged = request.encode();
      } on FormatException {
        staged = null;
      }
      expect(staged, v4);
      final expected = _expected(item);
      expect(v4 == null ? 'reject' : 'accept', expected['outcome']);
      if (v4 != null && expected.containsKey('normalized')) {
        expect(jsonDecode(v4), expected['normalized']);
      }
    });
  }
}

Map<String, Object?> _expected(Map<String, Object?> item) =>
    ((item['expect']! as Map)['dart']! as Map).cast<String, Object?>();

List<Map<String, Object?>> _profileCases() {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
  final folder = Directory(
    [
      directory.path,
      'tests',
      'fixtures',
      'gamemode-v6',
      'serialization',
      'transport',
    ].join(Platform.pathSeparator),
  );
  final files =
      folder
          .listSync()
          .whereType<File>()
          .where(
            (file) => file.uri.pathSegments.last.startsWith('wire-profile-'),
          )
          .toList()
        ..sort((left, right) => left.path.compareTo(right.path));
  return [
    for (final file in files)
      (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>(),
  ];
}
