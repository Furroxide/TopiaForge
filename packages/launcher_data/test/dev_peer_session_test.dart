import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'dev_peer_session_fixture.dart';

void main() {
  group('development peer lifetime', () {
    late Directory root;
    final fixtures = <PeerSessionFixture>[];
    setUp(() {
      root = Directory.systemTemp.createTempSync('tf peer quoted & café ');
      fixtures.clear();
    });
    tearDown(() async {
      for (final fixture in fixtures) {
        await fixture.retire();
      }
      root.deleteSync(recursive: true);
    });

    Future<DevPeerSession> prepare({
      bool omitIdentity = false,
      CancellationToken? cancellation,
    }) async {
      final fixture = PeerSessionFixture(root, fixtures.length)
        ..omitIdentity = omitIdentity;
      fixtures.add(fixture);
      final session = DevPeerSession(
        fixture.overlay,
        'a' * 32,
        root.path,
        repository: fixture,
        cancellation: cancellation,
      );
      await session.prepare(
        [],
        LauncherProfile(id: 'source', name: 'Source'),
        'example.mod',
        null,
      );
      return session;
    }

    test(
      'a started peer without an ownership receipt retains its files',
      () async {
        final session = await prepare(omitIdentity: true);
        await expectLater(session.launch(2), throwsStateError);
        expect(
          await isLaunchProcessAlive(fixtures.single.receipt!.identity!),
          isTrue,
        );
        expect(await session.isAlive(), isNull);
        await expectLater(session.close(), throwsStateError);
        expect(
          await isLaunchProcessAlive(fixtures.single.receipt!.identity!),
          isTrue,
        );
      },
    );

    test('cancelled preparation and launch do not start a peer', () async {
      final cancellation = CancellationToken();
      final session = await prepare(cancellation: cancellation);
      cancellation.cancel();
      await expectLater(
        session.prepare(
          [],
          LauncherProfile(id: 'source', name: 'Source'),
          'example.mod',
          null,
        ),
        throwsA(isA<OperationCancelledException>()),
      );
      await expectLater(
        session.launch(2),
        throwsA(isA<OperationCancelledException>()),
      );
      expect(fixtures.single.receipt, isNull);
      await session.close();
    });

    test(
      'two through eight inert peers keep distinct processes roots and exact log arguments',
      () async {
        for (final count in [2, 8]) {
          final sessions = <DevPeerSession>[];
          final start = fixtures.length;
          final run = Directory(p.join(root.path, 'run-$count'))..createSync();
          for (var index = 0; index < count; index++) {
            // Each iteration has its own valid index within this run.
            final fixture = PeerSessionFixture(run, index);
            fixtures.add(fixture);
            final session = DevPeerSession(
              fixture.overlay,
              'a' * 32,
              root.path,
              repository: fixture,
            );
            await session.prepare(
              [],
              LauncherProfile(id: 'source', name: 'Source'),
              'example.mod',
              null,
            );
            await session.launch(count);
            sessions.add(session);
            final report = jsonDecode(fixture.report.readAsStringSync()) as Map;
            expect(report['pid'], fixture.receipt!.pid);
            expect(report['root'], fixture.overlay.launcherRoot);
            expect(
              report['arguments'],
              fixture.savedProfile!.launchSettings.extraArguments,
            );
          }
          expect(
            fixtures.skip(start).map((f) => f.receipt!.pid).toSet(),
            hasLength(count),
          );
          await sessions.first.close();
          for (final session in sessions.skip(1)) {
            expect(await session.isAlive(), isTrue);
          }
          for (final session in sessions.skip(1)) {
            await session.close();
          }
          for (final session in sessions) {
            expect(await session.isAlive(), isFalse);
          }
        }
      },
    );

    test(
      'terminal launch failure or cancellation overrides stale ready progress',
      () async {
        final session = await prepare();
        await session.launch(2);
        final fixture = fixtures.single..ready();
        for (final status in ['failed', 'cancelled']) {
          fixture.channel(
            'launch-outcome',
            LaunchOutcome(
              kind: 'launch',
              requestId: fixture.requestId,
              command: 'launch-target',
              sequence: 2,
              status: status,
              phase: 'idle',
              error: status == 'failed'
                  ? LaunchOperationError(
                      code: 'invalidArgument',
                      message: 'fixture',
                    )
                  : null,
            ).toJson(),
          );
          await expectLater(session.isReady(), throwsStateError);
        }
        await session.close();
      },
    );

    test('terminal session outcome overrides stale ready progress', () async {
      final session = await prepare();
      await session.launch(2);
      final fixture = fixtures.single..ready();
      fixture.channel(
        'session-outcome',
        LaunchOutcome(
          kind: 'session',
          requestId: fixture.requestId,
          sessionId: 'finished',
          sequence: 2,
          status: 'succeeded',
          phase: 'idle',
        ).toJson(),
      );
      await expectLater(session.isReady(), throwsStateError);
      await session.close();
    });

    test(
      'stopped progress refuses readiness before an outcome arrives',
      () async {
        final session = await prepare();
        await session.launch(2);
        final fixture = fixtures.single;
        fixture.channel(
          'launch-progress',
          LaunchProgress(
            requestId: fixture.requestId,
            sequence: 2,
            phase: 'idle',
            session: LaunchProgressSession(
              role: LaunchSessionRole.host,
              intentId: 'fixture',
              state: LaunchSessionState.stopped,
            ),
          ).toJson(),
        );
        await expectLater(session.isReady(), throwsStateError);
        await session.close();
      },
    );

    test(
      'cancellation interrupts a stalled loopback descriptor fetch',
      () async {
        final cancellation = CancellationToken();
        final session = await prepare(cancellation: cancellation);
        await session.launch(2);
        final fixture = fixtures.single..ready();
        final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
        final connected = Completer<void>();
        final sockets = <Socket>[];
        final listener = server.listen((socket) {
          sockets.add(socket);
          socket.listen((_) {
            if (!connected.isCompleted) connected.complete();
          });
        });
        final receipt = File(
          p.join(
            fixture.gameRoot,
            'BepInEx',
            'TopiaForge',
            'data',
            'io.github.furroxide.topiaforge.multiplayer',
            'dev-session.json',
          ),
        );
        receipt.parent.createSync(recursive: true);
        receipt.writeAsStringSync(
          jsonEncode({
            'schemaVersion': 1,
            'runId': 'a' * 32,
            'port': server.port,
            'serverId': 'b' * 64,
          }),
        );
        final pending = session.hostTarget();
        try {
          await connected.future.timeout(const Duration(seconds: 3));
          cancellation.cancel();
          expect(await pending.timeout(const Duration(seconds: 1)), isNull);
        } finally {
          for (final socket in sockets) {
            socket.destroy();
          }
          await listener.cancel();
          await server.close();
          await pending;
          await session.close();
        }
      },
    );
  }, skip: !Platform.isWindows);
}
