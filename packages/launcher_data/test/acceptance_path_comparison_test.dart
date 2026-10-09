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

  test('acceptance equality follows host path case semantics', () {
    final upper = p.join(f.root.path, 'Game');
    final lower = p.join(f.root.path, 'game');
    expect(sameAcceptancePath(upper, lower), Platform.isWindows);
    expect(sameAcceptancePath(lower, upper), Platform.isWindows);
  });

  test('acceptance comparison retains normalized absolute equivalence', () {
    final path = p.join(f.root.path, 'Game');
    expect(sameAcceptancePath(path, p.relative(path)), isTrue);
    expect(sameAcceptancePath(path, p.join(path, 'unused', '..')), isTrue);
    // Raw admission separately rejects this alias even though it compares equal.
    expect(
      () => requireAcceptanceUnlinkedPath(p.join(path, 'unused', '..')),
      throwsFormatException,
    );
  });

  test('acceptance overlap uses host case and whole path components', () {
    final upper = p.join(f.root.path, 'Game');
    final lowerChild = p.join(f.root.path, 'game', 'child');
    expect(acceptancePathsOverlap(upper, lowerChild), Platform.isWindows);
    expect(acceptancePathsOverlap(lowerChild, upper), Platform.isWindows);
    expect(acceptancePathsOverlap(upper, p.join(upper, 'child')), isTrue);
    expect(acceptancePathsOverlap(upper, '${upper}Extra'), isFalse);
  });

  for (final argument in ['source', 'output']) {
    test('admission checks $argument argument with host case semantics', () {
      final recordBytes = f.record.readAsBytesSync();
      AcceptanceIsolationContext admit() => AcceptanceIsolationContext.admit(
        recordPath: f.record.path,
        sourceGameRoot: argument == 'source'
            ? p.join(f.root.path, 'SOURCE')
            : f.source.path,
        outputRoot: argument == 'output'
            ? p.join(f.root.path, 'OUTPUT')
            : f.output,
        identityReader: () => f.identity,
      );
      if (Platform.isWindows) {
        expect(admit, returnsNormally);
      } else {
        expect(admit, throwsStateError);
      }
      expect(f.record.readAsBytesSync(), recordBytes);
      expect(Directory(f.output).existsSync(), isFalse);
    });
  }

  test('persistent data containment follows host case semantics', () {
    f.change((raw) {
      raw['persistentDataRoot'] = p.join(
        f.identity.userProfile,
        'locallow',
        'Vendor',
        'Game',
      );
    });
    if (Platform.isWindows) {
      expect(f.admit, returnsNormally);
    } else {
      expect(f.admit, throwsStateError);
    }
  });

  test('ACK cannot substitute a case-distinct host executable path', () async {
    final store = LaunchStagingStore(f.game.path);
    final profileFile = await store.writeRequest(f.profile);
    final request = await store.writeAcceptanceRequest(
      context: f.admit(),
      profile: f.profile,
      challenge: 'a' * 64,
      now: DateTime.now().toUtc(),
    );
    final ack = f.ack(request);
    (ack['process']! as Map)['executablePath'] = p.join(
      f.game.path,
      'game.exe',
    );
    final bytes = utf8.encode(jsonEncode(ack));
    final ackFile = f.channel('acceptance-isolation-ack')
      ..writeAsBytesSync(bytes);
    final result = store.readAcceptanceAcknowledgement(request, f.process);
    if (Platform.isWindows) {
      expect(await result, isNotNull);
    } else {
      await expectLater(result, throwsStateError);
    }
    expect(ackFile.readAsBytesSync(), bytes);
    expect(profileFile.existsSync(), isTrue);
  });
}
