import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// Replays tests/fixtures/multiplayer-admission/cases.json, which the C#
/// planner replays too, so both languages must produce identical admission
/// reports, messages and order included.
void main() {
  final document = _readFixture();
  final defaults = _object(document['defaults']);
  final manifestBase = _object(document['manifestBase']);
  final mods = {
    for (final entry in _object(document['mods']).entries)
      entry.key: _readMod(manifestBase, _object(entry.value)),
  };
  final cases = (document['cases']! as List<Object?>).map(_object).toList();

  test('fixture is well formed', () {
    expect(document['schemaVersion'], 1);
    expect(cases, isNotEmpty);
    final names = cases.map((item) => item['name']! as String).toList();
    expect(names.toSet(), hasLength(names.length));
  });

  for (final testCase in cases) {
    test(testCase['name']! as String, () {
      final report = const MultiplayerAdmissionPlanner().evaluate(
        server: _readProfile(
          'server',
          _object(testCase['server']),
          defaults,
          mods,
        ),
        client: _readProfile(
          'client',
          _object(testCase['client']),
          defaults,
          mods,
        ),
        policy: MultiplayerAdmissionPolicy.values.byName(
          testCase['policy']! as String,
        ),
        serverRole: MultiplayerAdmissionServerRole.values.byName(
          testCase['serverRole']! as String,
        ),
      );
      final expected = _object(testCase['expect']);
      expect(report.isAdmitted, expected['admitted']);
      expect(report.activeSessionMods, expected['activeSessionMods']);
      expect(
        report.mismatches.map(_describe).toList(),
        _expectedRows(expected['mismatches']),
      );
      expect(
        report.inactiveSessionReasons.map(_describe).toList(),
        _expectedRows(expected['inactiveSessionReasons']),
      );
    });
  }
}

MultiplayerAdmissionMod _readMod(
  Map<String, Object?> manifestBase,
  Map<String, Object?> mod,
) {
  // Round-trip through JSON text so each manifest is read exactly as a file.
  final manifest = jsonDecode(
    jsonEncode({...manifestBase, ..._object(mod['manifest'])}),
  );
  return MultiplayerAdmissionMod(
    manifest: ModManifest.fromJson(_object(manifest)),
    packageSha256: mod['packageSha256']! as String,
  );
}

MultiplayerAdmissionProfile _readProfile(
  String peerId,
  Map<String, Object?> profile,
  Map<String, Object?> defaults,
  Map<String, MultiplayerAdmissionMod> mods,
) {
  String field(String key) => (profile[key] ?? defaults[key])! as String;
  return MultiplayerAdmissionProfile(
    peerId: peerId,
    gameBuild: field('gameBuild'),
    topiaForgeProtocolVersion: field('protocolVersion'),
    topiaForgePeerVersionRange: field('peerVersionRange'),
    mods: [for (final key in profile['mods']! as List<Object?>) mods[key]!],
  );
}

List<String> _expectedRows(Object? rows) => [
  for (final row in (rows! as List<Object?>).map(_object))
    [
      row['code'],
      row['modId'],
      row['message'],
      row['serverValue'],
      row['clientValue'],
    ].join(' | '),
];

String _describe(MultiplayerAdmissionMismatch item) => [
  item.code.name,
  item.modId,
  item.message,
  item.serverValue,
  item.clientValue,
].join(' | ');

Map<String, Object?> _readFixture() {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
  final path = [
    directory.path,
    'tests',
    'fixtures',
    'multiplayer-admission',
    'cases.json',
  ].join(Platform.pathSeparator);
  return _object(jsonDecode(File(path).readAsStringSync()));
}

Map<String, Object?> _object(Object? value) =>
    (value! as Map).map((key, item) => MapEntry(key as String, item));
