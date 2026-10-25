import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';
import 'package:topiaforge/src/live_acceptance_trust.dart';
import 'package:topiaforge/src/multiplayer_acceptance_runner.dart';
import 'package:topiaforge/src/multiplayer_acceptance_spec.dart';
import 'live_acceptance_test_fixture.dart';

void main() {
  late _Pair fixture;
  setUp(() => fixture = _Pair());
  tearDown(() => fixture.dispose());
  test(
    'two peers launch host first and close in reverse order with exact receipts',
    () async {
      final result = await fixture.run();
      expect(result['succeeded'], isTrue);
      expect(result['processExitConfirmed'], isTrue);
      expect(fixture.events, [
        'launch:host',
        'confirm:host',
        'launch:guest',
        'confirm:guest',
        'close:guest',
        'close:host',
      ]);
      expect(fixture.commands.length, 6);
      expect((result['isolation'] as Map).length, 2);
    },
  );
  test('guest confirmation failure still closes both owned peers', () async {
    fixture.failConfirm = 'guest';
    await expectLater(fixture.run(), throwsStateError);
    expect(fixture.events.sublist(fixture.events.length - 2), [
      'close:guest',
      'close:host',
    ]);
  });
  test(
    'one cleanup failure does not prevent cleanup of the other peer',
    () async {
      fixture.failClose = 'guest';
      await expectLater(fixture.run(), throwsStateError);
      expect(fixture.events.last, 'close:host');
      expect(
        File(
          p.join(
            fixture.host.output.path,
            'multiplayer-acceptance-result.json',
          ),
        ).existsSync(),
        isFalse,
      );
    },
  );
  test('one wrong package receipt prevents live success', () async {
    fixture.tamper = true;
    await expectLater(fixture.run(), throwsStateError);
    final result =
        jsonDecode(
              File(
                p.join(
                  fixture.host.output.path,
                  'multiplayer-acceptance-result.json',
                ),
              ).readAsStringSync(),
            )
            as Map;
    expect(result['succeeded'], isFalse);
    expect(result['receiptsMatch'], isFalse);
  });
  test(
    'missing guest observation times out and cleans both processes',
    () async {
      fixture.omitGuest = true;
      await expectLater(fixture.run(), throwsStateError);
      expect(fixture.events.last, 'close:host');
    },
  );
  test('ordinary SDK package cannot stand in for multiplayer probe', () async {
    fixture.host.package.writeAsBytesSync(
      acceptancePackageBytes('dev.topiaforge.sdk-acceptance'),
    );
    await expectLater(fixture.run(), throwsStateError);
    expect(fixture.admissions, 0);
    expect(fixture.commands, isEmpty);
    expect(fixture.events, isEmpty);
  });
  test('a missing probe is refused before admission or any effect', () async {
    fixture.host.package.deleteSync();
    await expectLater(
      fixture.run(),
      throwsA(
        isA<MultiplayerAcceptanceRefusal>().having(
          (refusal) => refusal.message,
          'message',
          startsWith(
            'TFMPACCEPT110: The multiplayer acceptance probe package is missing.',
          ),
        ),
      ),
    );
    expect(fixture.admissions, 0);
    expect(fixture.commands, isEmpty);
    expect(fixture.events, isEmpty);
  });
  test(
    'unadmitted guest prevents all installation and launch effects',
    () async {
      await expectLater(fixture.run(refuseGuest: true), throwsStateError);
      expect(fixture.commands, isEmpty);
      expect(fixture.events, isEmpty);
    },
  );
}

final class _Pair {
  final host = AcceptanceFixture();
  final guest = AcceptanceFixture();
  final commands = <List<String>>[];
  final events = <String>[];
  var now = DateTime.utc(2026, 1, 1);
  var admissions = 0;
  String? failConfirm, failClose;
  bool tamper = false, omitGuest = false;
  _Pair() {
    host.package.writeAsBytesSync(
      acceptancePackageBytes(MultiplayerAcceptanceSpec.packageId),
    );
  }
  Future<Map<String, Object?>> run({bool refuseGuest = false}) {
    final spec = MultiplayerAcceptanceSpec.decode(
      jsonEncode({
        'schemaVersion': 1,
        'topology': 'listen-pair',
        'packageId': MultiplayerAcceptanceSpec.packageId,
        'cases': [
          {
            'id': 'multiplayer.lan',
            'mode': 'automatic',
            'peers': ['host', 'guest'],
            'steps': 'Join.',
          },
        ],
      }),
    );
    return MultiplayerAcceptanceRunner(
      command: (args, _) async {
        commands.add(args);
        return 0;
      },
      admitPeer: (peer, _) {
        admissions++;
        if (peer == 'guest' && refuseGuest) throw StateError('Guest refused.');
        final f = peer == 'host' ? host : guest;
        return f.admitIsolation(f.options());
      },
      clock: () => now,
      delay: (duration) async {
        now = now.add(duration);
      },
      launchPeer: (_, context, challenge, peer) async {
        events.add('launch:$peer');
        writeRun(context, challenge, peer);
        return _Session(peer, events, failConfirm == peer, failClose == peer);
      },
    ).run(
      MultiplayerAcceptanceOptions(
        repositoryRoot: host.repository.path,
        sourceGameRoot: host.sourceGame.path,
        hostRecord: '',
        guestRecord: '',
        outputRoot: host.output.path,
        packagePath: host.package.path,
        timeout: const Duration(seconds: 30),
      ),
      spec,
    );
  }

  void writeRun(
    AcceptanceIsolationContext context,
    String challenge,
    String peer,
  ) {
    final logs = Directory(p.join(context.managerRoot, 'logs'))
      ..createSync(recursive: true);
    if (!(peer == 'guest' && omitGuest)) {
      File(p.join(logs.path, 'manager.log')).writeAsStringSync(
        '${now.toIso8601String()} [INFO] [${MultiplayerAcceptanceSpec.packageId}] '
        'TF-MP-ACCEPT|PASS|$challenge|$peer|multiplayer.lan|automatic\n',
      );
    }
    final receipt = readLiveAcceptancePackageReceipt(
      host.package.path,
    ).toJson();
    if (peer == 'guest' && tamper) receipt['sourceSha256'] = '0' * 64;
    File(p.join(logs.path, 'last-run.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 1,
        'completedAtUtc': now.toIso8601String(),
        'sessionId': 'test-$peer',
        'rootError': '',
        'packages': [
          {
            'id': MultiplayerAcceptanceSpec.packageId,
            'valid': true,
            'status': 'loaded',
            ...receipt,
          },
        ],
      }),
    );
  }

  void dispose() {
    host.dispose();
    guest.dispose();
  }
}

final class _Session implements LiveAcceptanceSession {
  _Session(this.peer, this.events, this.failConfirm, this.failClose);
  final String peer;
  final List<String> events;
  final bool failConfirm, failClose;
  bool confirmed = false, closed = false;
  @override
  Future<void> confirm(Duration timeout) async {
    events.add('confirm:$peer');
    if (failConfirm) throw StateError('Synthetic confirmation failure.');
    confirmed = true;
  }

  @override
  Future<void> close() async {
    events.add('close:$peer');
    if (failClose) throw StateError('Synthetic cleanup failure.');
    closed = true;
  }

  @override
  Map<String, Object?> get isolationEvidence {
    if (!closed || !confirmed) throw StateError('Synthetic unconfirmed peer.');
    return {'testFixture': true, 'processExitConfirmed': true};
  }
}
