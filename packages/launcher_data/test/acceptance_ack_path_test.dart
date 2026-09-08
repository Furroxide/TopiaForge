import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'acceptance_isolation_fixture.dart';

void main() {
  late IsolationFixture f;
  setUp(() => f = IsolationFixture());
  tearDown(() => f.dispose());

  const fields = [
    ('process', 'executablePath'),
    ('observedOsIdentity', 'userProfile'),
    ('observedOsIdentity', 'localAppDataLow'),
    ('observedRoots', 'gameRoot'),
    ('observedRoots', 'bepInExRoot'),
    ('observedRoots', 'managerRoot'),
    ('observedRoots', 'persistentDataRoot'),
  ];
  for (final field in fields) {
    for (final alias in ['relative', 'dot-segment']) {
      test('ACK rejects raw $alias path in ${field.$1}.${field.$2}', () async {
        final store = LaunchStagingStore(f.game.path);
        final profileFile = await store.writeRequest(f.profile);
        final request = await store.writeAcceptanceRequest(
          context: f.admit(),
          profile: f.profile,
          challenge: 'a' * 64,
          now: DateTime.now().toUtc(),
        );
        final ack = f.ack(request);
        final object = ack[field.$1]! as Map<String, Object?>;
        final original = object[field.$2]! as String;
        final replacement = alias == 'relative'
            ? p.relative(original)
            : '${p.dirname(original)}${p.separator}discard${p.separator}..'
                  '${p.separator}${p.basename(original)}';
        expect(
          sameAcceptancePath(replacement, original),
          isTrue,
          reason: 'Comparison alone must not erase forbidden raw aliases.',
        );
        object[field.$2] = replacement;
        final bytes = utf8.encode(jsonEncode(ack));
        final ackFile = f.channel('acceptance-isolation-ack')
          ..writeAsBytesSync(bytes);
        await expectLater(
          store.readAcceptanceAcknowledgement(request, f.process),
          throwsFormatException,
        );
        expect(ackFile.readAsBytesSync(), bytes);
        expect(profileFile.existsSync(), isTrue);
      });
    }
  }
  test(
    'canonical Windows image path permits case and slash equivalence',
    () async {
      final store = LaunchStagingStore(f.game.path);
      await store.writeRequest(f.profile);
      final request = await store.writeAcceptanceRequest(
        context: f.admit(),
        profile: f.profile,
        challenge: 'a' * 64,
        now: DateTime.now().toUtc(),
      );
      final ack = f.ack(request);
      (ack['process']! as Map)['executablePath'] = f.process.executablePath
          .toUpperCase()
          .replaceAll(r'\', '/');
      f.channel('acceptance-isolation-ack').writeAsStringSync(jsonEncode(ack));
      expect(
        await store.readAcceptanceAcknowledgement(request, f.process),
        isNotNull,
      );
    },
    skip: !Platform.isWindows,
  );
}
