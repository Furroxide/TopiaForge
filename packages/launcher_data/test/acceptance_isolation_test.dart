import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/acceptance_isolation_bootstrap.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'acceptance_isolation_fixture.dart';

void main() {
  late IsolationFixture f;
  setUp(() => f = IsolationFixture());
  tearDown(() => f.dispose());
  test(
    'admission is read-only and verifies measured identity on every use',
    () {
      final before = f.root
          .listSync(recursive: true)
          .map((e) => e.path)
          .toList();
      final context = f.admit();
      context.verify();
      expect(f.root.listSync(recursive: true).map((e) => e.path), before);
      f.change((raw) => raw['reviewerEvidence'] = 'changed');
      expect(context.verify, throwsStateError);
      expect(Directory(f.launcher).existsSync(), isFalse);
    },
  );
  for (final change in <String, void Function(Map<String, Object?>)>{
    'same OS user': (raw) => raw['normalUserSid'] = raw['userSid'],
    'wrong OS user': (raw) => raw['userSid'] = 'S-1-5-21-999',
    'wrong known folder': (raw) => raw['localAppDataLow'] = f.source.path,
    'normal installation write': (raw) => raw['gameRoot'] = f.source.path,
    'normal profile write': (raw) =>
        raw['outputRoot'] = raw['normalUserProfile'],
    'native data outside QA': (raw) => raw['persistentDataRoot'] = f.output,
    'null identity': (raw) => raw['userSid'] = null,
    'fractional version': (raw) => raw['schemaVersion'] = 1.0,
    'unknown field': (raw) => raw['skipIdentityCheck'] = true,
    'unreviewed record': (raw) => raw['reviewerEvidence'] = '',
  }.entries) {
    test('refuses ${change.key} without writable root creation', () {
      f.change(change.value);
      expect(f.admit, throwsA(isA<Object>()));
      expect(Directory(f.output).existsSync(), isFalse);
      expect(Directory(f.launcher).existsSync(), isFalse);
      expect(Directory(p.join(f.source.path, 'BepInEx')).existsSync(), isFalse);
    });
  }
  test(
    'duplicate private record keys cannot erase earlier identity values',
    () {
      f.record.writeAsStringSync(
        f.record.readAsStringSync().replaceFirst('{', '{"userSid":"foreign",'),
      );
      expect(f.admit, throwsFormatException);
    },
  );
  test('bootstrap refuses altered loader config before temporary writes', () {
    File(
      p.join(f.game.path, 'doorstop_config.ini'),
    ).writeAsStringSync('[UnityDoorstop]\ntarget_assembly=outside.dll');
    expect(
      () => acceptanceLaunchEnvironment(f.admit(), 'profile', 'request'),
      throwsStateError,
    );
    expect(Directory(f.launcher).existsSync(), isFalse);
  });
  test('pinned bootstrap bytes match the bundled BepInEx input', () {
    final file = File(
      p.join(
        Directory.current.path,
        '..',
        '..',
        'third_party',
        'BepInEx',
        'win_x64_5.4.23.5',
        'doorstop_config.ini',
      ),
    );
    expect(
      sha256.convert(file.readAsBytesSync()).toString(),
      acceptanceDoorstopSha256,
    );
  });
  test('admission refuses linked loader roots before configuration writes', () {
    final link = Link(p.join(f.game.path, 'BepInEx'));
    try {
      link.createSync(f.source.path);
    } on FileSystemException {
      markTestSkipped('OS symlink creation is unavailable.');
      return;
    }
    try {
      expect(f.admit, throwsFormatException);
    } finally {
      link.deleteSync();
    }
  });
  for (final component in [
    'normal.',
    'normal ',
    'config:stream',
    'NUL',
    'COM1.json',
  ]) {
    test(
      'rejects ambiguous Windows component $component before filesystem effects',
      () {
        expect(
          () => requireAcceptanceUnlinkedPath(p.join(f.root.path, component)),
          throwsFormatException,
        );
      },
      skip: !Platform.isWindows,
    );
  }
  test('ordinary basename with adjacent dots is an unambiguous local path', () {
    final path = p.join(f.root.path, 'ordinary..name');
    expect(() => requireAcceptanceUnlinkedPath(path), returnsNormally);
  });
  test(
    'sidecar binds exact V4 bytes and permits equivalent Windows ACK paths',
    () async {
      final context = f.admit();
      final store = LaunchStagingStore(f.game.path);
      final profile = await store.writeRequest(f.profile);
      final request = await store.writeAcceptanceRequest(
        context: context,
        profile: f.profile,
        challenge: 'a' * 64,
        now: DateTime.now().toUtc(),
      );
      expect(
        request.document['profileRequestSha256'],
        sha256.convert(profile.readAsBytesSync()).toString(),
      );
      expect(
        await store.readAcceptanceAcknowledgement(request, f.process),
        isNull,
      );
      final ack = f.ack(request);
      if (Platform.isWindows) {
        for (final name in ['observedRoots', 'observedOsIdentity']) {
          final fields = ack[name]! as Map<String, Object?>;
          for (final key in fields.keys.toList()) {
            if (fields[key] is String &&
                (fields[key]! as String).contains(':')) {
              fields[key] = (fields[key]! as String).toUpperCase().replaceAll(
                '\\',
                '/',
              );
            }
          }
        }
      }
      final bytes = utf8.encode(jsonEncode(ack));
      f.channel('acceptance-isolation-ack').writeAsBytesSync(bytes);
      final observed = await store.readAcceptanceAcknowledgement(
        request,
        f.process,
      );
      expect(observed!.sha256Digest, sha256.convert(bytes).toString());
      expect(observed.document['requestId'], f.profile.requestId);
      expect(
        profile.existsSync(),
        isTrue,
        reason: 'Private ACK reads do not consume public V4.',
      );
    },
  );
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'stale challenge': (raw) => raw['challenge'] = 'b' * 64,
    'foreign request digest': (raw) => raw['requestSha256'] = 'b' * 64,
    'wrong native generation': (raw) =>
        (raw['process']! as Map)['nativeStartToken'] = 'windows:9',
    'PID scalar coercion': (raw) => (raw['process']! as Map)['pid'] = '4242',
    'fractional session': (raw) =>
        (raw['observedOsIdentity']! as Map)['sessionId'] = 2.0,
    'foreign OS identity': (raw) =>
        (raw['observedOsIdentity']! as Map)['userSid'] = 'S-1-5-21-9',
    'unknown root': (raw) => (raw['observedRoots']! as Map)['other'] = 'x',
    'native data mismatch': (raw) =>
        (raw['observedRoots']! as Map)['persistentDataRoot'] = f.output,
    'refused runtime': (raw) => raw['status'] = 'rejected',
  }.entries) {
    test('ACK rejects ${mutation.key}', () async {
      final store = LaunchStagingStore(f.game.path);
      await store.writeRequest(f.profile);
      final request = await store.writeAcceptanceRequest(
        context: f.admit(),
        profile: f.profile,
        challenge: 'a' * 64,
        now: DateTime.now().toUtc(),
      );
      final ack = f.ack(request);
      mutation.value(ack);
      f.channel('acceptance-isolation-ack').writeAsStringSync(jsonEncode(ack));
      await expectLater(
        store.readAcceptanceAcknowledgement(request, f.process),
        throwsA(isA<Object>()),
      );
    });
  }
}
