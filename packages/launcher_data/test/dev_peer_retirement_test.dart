import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'dev_peer_session_fixture.dart';

void main() {
  group('peer fixture retirement', () {
    late Directory root;
    late LaunchProcessIdentity identity;
    late PeerSessionFixture fixture;
    late List<String> calls;

    setUp(() {
      root = Directory.systemTemp.createTempSync('tf-peer-retirement-');
      identity = LaunchProcessIdentity(
        pid: 42,
        startTimeUtc: DateTime.utc(2026, 1, 1),
        executablePath: p.join(root.path, 'inert-vm.exe'),
        nativeStartToken: 'fixture-generation',
      );
      calls = [];
    });
    tearDown(() async {
      // These cases never launch a process; release the real repository even
      // when its scripted retirement was deliberately refused.
      await fixture.dispose();
      if (root.existsSync()) root.deleteSync(recursive: true);
    });

    void prepare({
      required Future<bool?> Function() probe,
      required Future<bool> Function() stop,
    }) {
      fixture = PeerSessionFixture(
        root,
        0,
        checkAlive: (actual) async {
          expect(actual, same(identity));
          calls.add('probe');
          return await probe();
        },
        stopOwnedProcess: (actual) async {
          expect(actual, same(identity));
          calls.add('stop');
          return await stop();
        },
        disposeRepository: () async {
          calls.add('dispose');
          await fixture.dispose();
        },
      )..receipt = LaunchProcessReceipt(pid: identity.pid, identity: identity);
    }

    test('a false observation is terminal without a second probe', () async {
      final observations = <bool?>[false, null];
      prepare(
        probe: () async => observations.removeAt(0),
        stop: () async => throw StateError('Stop must not follow exit proof.'),
      );

      await fixture.retire();

      expect(calls, ['probe', 'dispose']);
      expect(observations, [null]);
    });

    test('a live observation waits for the exact stop to complete', () async {
      final entered = Completer<void>();
      final stopped = Completer<bool>();
      prepare(
        probe: () async => true,
        stop: () {
          entered.complete();
          return stopped.future;
        },
      );

      final retiring = fixture.retire();
      await entered.future;
      expect(calls, ['probe', 'stop']);
      stopped.complete(true);
      await retiring;

      expect(calls, ['probe', 'stop', 'dispose']);
    });

    test('unknown liveness accepts an exact stop finding absence', () async {
      prepare(probe: () async => null, stop: () async => false);

      await fixture.retire();

      expect(calls, ['probe', 'stop', 'dispose']);
    });

    test(
      'a stop refusal propagates before disposal or root deletion',
      () async {
        final refusal = StateError('Exact process ownership was refused.');
        prepare(probe: () async => null, stop: () async => throw refusal);
        final retained = File(p.join(root.path, 'retained.txt'))
          ..writeAsStringSync('owned');

        Future<void> retireAndDelete() async {
          await fixture.retire();
          root.deleteSync(recursive: true);
        }

        await expectLater(retireAndDelete(), throwsA(same(refusal)));

        expect(calls, ['probe', 'stop']);
        expect(retained.readAsStringSync(), 'owned');
      },
    );

    test('a receipt without identity refuses retirement', () async {
      prepare(probe: () async => false, stop: () async => false);
      fixture.receipt = LaunchProcessReceipt(pid: identity.pid);

      await expectLater(fixture.retire(), throwsStateError);

      expect(calls, isEmpty);
      expect(root.existsSync(), isTrue);
    });

    test('no launch receipt needs no process observation or stop', () async {
      prepare(probe: () async => false, stop: () async => false);
      fixture.receipt = null;

      await fixture.retire();

      expect(calls, ['dispose']);
    });

    test('a probe failure propagates before stop or disposal', () async {
      final failure = StateError('Liveness observation failed.');
      prepare(probe: () async => throw failure, stop: () async => false);

      await expectLater(fixture.retire(), throwsA(same(failure)));

      expect(calls, ['probe']);
      expect(root.existsSync(), isTrue);
    });
  });
}
