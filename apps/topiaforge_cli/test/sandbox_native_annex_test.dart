import 'dart:convert';
import 'dart:io';
import 'package:test/test.dart';
import 'package:topiaforge/src/release_strict_json.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex.dart';
import 'sandbox_native_fixture.dart';

void main() {
  test('post-admission failure fixture has schema parity', () {
    final value = nativeAnnexFixture();
    final schema =
        jsonDecode(
              File(
                '../../schemas/topiaforge.sandbox-workbench-automation-v1.schema.json',
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    validateReleaseSchema(value, schema, 'test-only native fixture');
    final parsed = SandboxNativeAnnex.parse(nativeTestBytes(value));
    expect(parsed.cleanup['originalProcessExitConfirmed'], false);
    expect(parsed.scenarioResults, hasLength(9));
  });
  final invalid = <String, void Function(Map<String, Object?>)>{
    'unknown root': (v) => v['qualifiesRelease'] = true,
    'missing root': (v) => v.remove('challenge'),
    'floating version': (v) => v['schemaVersion'] = 1.0,
    'wrong kind': (v) => v['kind'] = 'live-acceptance',
    'release scope': (v) => v['scope'] = 'release',
    'bad run id': (v) => v['runId'] = 'Run With Spaces',
    'uppercase source': (v) => v['sourceRevision'] = 'B' * 40,
    'short challenge': (v) => v['challenge'] = 'a' * 63,
    'UTC offset': (v) => v['startedAtUtc'] = '2026-09-09T10:00:00+00:00',
    'invalid calendar': (v) => v['startedAtUtc'] = '2026-02-30T10:00:00Z',
    'reversed dates': (v) => v['completedAtUtc'] = '2026-09-09T09:00:00Z',
    'overlong run': (v) => v['completedAtUtc'] = '2026-09-09T17:00:00Z',
    'null ack': (v) => nativeMap(v['isolation'])['acknowledgement'] = null,
    'refused ack': (v) => ack(v)['status'] = 'refused',
    'stale ack': (v) => ack(v)['challenge'] = '0' * 64,
    'ack unknown field': (v) => ack(v)['forged'] = true,
    'ack reasons': (v) => ack(v)['reasons'] = ['denied'],
    'bad PID marker': (v) =>
        nativeMap(ack(v)['process'])['nativeStartToken'] = 'pid:1234',
    'zero game PID': (v) => nativeMap(ack(v)['process'])['pid'] = 0,
    'session zero': (v) =>
        nativeMap(ack(v)['observedOsIdentity'])['sessionId'] = 0,
    'relative executable': (v) =>
        nativeMap(ack(v)['process'])['executablePath'] = 'Robotopia.exe',
    'no packages': (v) => v['packages'] = [],
    'duplicate package': (v) =>
        nativeList(v['packages']).add(nativeList(v['packages']).first),
    'empty critical files': (v) =>
        nativeMap(nativeList(v['packages']).first)['criticalFiles'] = [],
    'case duplicate critical path': (v) => nativeList(
      nativeMap(nativeList(v['packages']).first)['criticalFiles'],
    ).add({'path': 'PLUGIN.dll', 'sha256': 'a' * 64}),
    'zero broker PID': (v) => nativeMap(v['broker'])['processId'] = 0,
    'artifact unknown field': (v) => artifact(v)['privateProfile'] = 'leak',
    'zero artifact length': (v) => artifact(v)['length'] = 0,
    'oversized artifact': (v) => artifact(v)['length'] = 268435457,
    'artifact aliases transcript': (v) =>
        artifact(v)['path'] = 'BROKER-TRANSCRIPT.JSON',
    'scenario out of order': (v) => nativeList(
      v['scenarioResults'],
    ).insert(0, nativeList(v['scenarioResults']).removeLast()),
    'unknown scenario': (v) =>
        nativeMap(nativeList(v['scenarioResults']).first)['scenarioId'] =
            'invented-case',
    'unknown status': (v) =>
        nativeMap(nativeList(v['scenarioResults']).first)['status'] = 'skipped',
    'missing refusal reason': (v) =>
        nativeMap(nativeList(v['scenarioResults']).first)['reason'] = '',
    'cleanup integer': (v) => nativeMap(v['cleanup'])['inputReleased'] = 1,
    'isolation exit string': (v) =>
        nativeMap(v['isolation'])['processExitConfirmed'] = 'true',
    'duplicate failures': (v) => v['failures'] = ['same', 'same'],
    'oversize text': (v) => v['failures'] = ['x' * 1025],
  };
  for (final entry in invalid.entries) {
    test('rejects ${entry.key}', () {
      final value = nativeAnnexFixture();
      entry.value(value);
      expect(
        () => SandboxNativeAnnex.parse(nativeTestBytes(value)),
        throwsStateError,
      );
    });
  }
  for (final path in [
    '../escape.json',
    '/absolute.json',
    r'C:\file.json',
    r'dir\file.json',
    'dir//file.json',
    'dir/./file.json',
    'dir/../file.json',
    'dir./file.json',
    'NUL.txt',
    'dir/COM1',
    'file.json:stream',
  ]) {
    test('rejects artifact path $path', () {
      final value = nativeAnnexFixture();
      artifact(value)['path'] = path;
      expect(
        () => SandboxNativeAnnex.parse(nativeTestBytes(value)),
        throwsStateError,
      );
    });
  }
  test('nested containers are immutable', () {
    final parsed = SandboxNativeAnnex.parse(
      nativeTestBytes(nativeAnnexFixture()),
    );
    expect(() => parsed.document['runId'] = 'changed', throwsUnsupportedError);
    expect(
      () => parsed.cleanup['inputReleased'] = true,
      throwsUnsupportedError,
    );
    expect(
      () => parsed.scenarioResults.first['status'] = 'passed',
      throwsUnsupportedError,
    );
    expect(
      () => (parsed.document['artifacts']! as List).clear(),
      throwsUnsupportedError,
    );
    expect(
      () => nativeMap(
        nativeList(parsed.packages.first['criticalFiles']).first,
      )['sha256'] = '0' * 64,
      throwsUnsupportedError,
    );
  });
  test('rejects duplicate JSON', () {
    final text = jsonEncode(
      nativeAnnexFixture(),
    ).replaceFirst('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1');
    expect(() => SandboxNativeAnnex.parse(utf8.encode(text)), throwsStateError);
  });
  test(
    'rejects invalid UTF8',
    () => expect(() => SandboxNativeAnnex.parse([255, 255]), throwsStateError),
  );
  test(
    'rejects oversized document',
    () => expect(
      () => SandboxNativeAnnex.parse(List.filled(262145, 32)),
      throwsStateError,
    ),
  );
  test('missing claims stay representable', () {
    final value = nativeAnnexFixture()..['scenarioResults'] = [];
    expect(
      SandboxNativeAnnex.parse(nativeTestBytes(value)).scenarioResults,
      isEmpty,
    );
  });
}

Map<String, Object?> ack(Map<String, Object?> value) =>
    nativeMap(nativeMap(value['isolation'])['acknowledgement']);
Map<String, Object?> artifact(Map<String, Object?> value) =>
    nativeMap(nativeList(value['artifacts']).first);
