import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/control_plane_pacing.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';

/// The launcher never sends faster than the service's limits allow
/// (ControlPlaneApi.md, section 1), counting its own requests as the
/// service counts them.
void main() {
  final exchanges = RecordedExchanges.load();

  group('the pacer', () {
    late FakeTime time;
    late ControlPlanePacer pacer;
    setUp(() {
      time = FakeTime();
      pacer = ControlPlanePacer(clock: time.call);
    });

    test('lets 60 browsing requests through in any two minutes, then holds '
        'until the oldest leaves', () {
      for (var request = 0; request < 60; request++) {
        expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
        pacer.take(ControlPlaneScope.browse);
        time.advance(const Duration(seconds: 1));
      }
      // The first request went out 60 s ago; it leaves the two-minute span
      // 60 s from now.
      expect(pacer.holdFor(ControlPlaneScope.browse), (
        const Duration(seconds: 60),
        ControlPlaneHold.rateLimited,
      ));
      time.advance(const Duration(seconds: 60));
      expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
    });

    test('lets 1,200 browsing requests through in any two hours', () {
      for (var round = 0; round < 20; round++) {
        for (var request = 0; request < 60; request++) {
          expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
          pacer.take(ControlPlaneScope.browse);
        }
        time.advance(const Duration(minutes: 2));
      }
      // 40 minutes in: the minute window is clear, the hour's is not.
      final (wait, why) = pacer.holdFor(ControlPlaneScope.browse)!;
      expect(why, ControlPlaneHold.rateLimited);
      expect(wait, const Duration(minutes: 80));
      time.advance(wait);
      expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
    });

    test('holds an invite resolution\'s slot while it runs, and keeps it only '
        'when it missed', () {
      final slots = [
        for (var request = 0; request < 10; request++)
          pacer.take(ControlPlaneScope.invite),
      ];
      expect(
        pacer.holdFor(ControlPlaneScope.invite)?.$2,
        ControlPlaneHold.rateLimited,
      );
      // A hit gives its slot back.
      pacer.giveBack(slots.first);
      expect(pacer.holdFor(ControlPlaneScope.invite), isNull);
      // Scopes are paced apart.
      expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
      expect(pacer.holdFor(ControlPlaneScope.status), isNull);
    });

    test('never holds the status by count', () {
      for (var request = 0; request < 500; request++) {
        pacer.take(ControlPlaneScope.status);
      }
      expect(pacer.holdFor(ControlPlaneScope.status), isNull);
    });

    test('honours a named delay in full, whatever answers come', () {
      pacer.holdOff(
        ControlPlaneScope.invite,
        const Duration(seconds: 30),
        ControlPlaneHold.rateLimited,
      );
      pacer.answered(ControlPlaneScope.invite);
      time.advance(const Duration(seconds: 10));
      expect(pacer.holdFor(ControlPlaneScope.invite), (
        const Duration(seconds: 20),
        ControlPlaneHold.rateLimited,
      ));
      // A shorter delay named later does not shorten it.
      pacer.holdOff(
        ControlPlaneScope.invite,
        const Duration(seconds: 5),
        ControlPlaneHold.unavailable,
      );
      expect(
        pacer.holdFor(ControlPlaneScope.invite)!.$1,
        const Duration(seconds: 20),
      );
    });

    test('backs off for twice as long after each failure, up to five minutes, '
        'and an answer ends the run', () {
      final waits = [
        for (var failure = 0; failure < 12; failure++)
          pacer.failed(ControlPlaneScope.browse),
      ];
      expect(waits.take(5), const [
        Duration(seconds: 1),
        Duration(seconds: 2),
        Duration(seconds: 4),
        Duration(seconds: 8),
        Duration(seconds: 16),
      ]);
      expect(waits.last, ControlPlanePacer.longestBackoff);
      expect(pacer.holdFor(ControlPlaneScope.browse), (
        ControlPlanePacer.longestBackoff,
        ControlPlaneHold.unavailable,
      ));
      pacer.answered(ControlPlaneScope.browse);
      expect(pacer.holdFor(ControlPlaneScope.browse), isNull);
      expect(
        pacer.failed(ControlPlaneScope.browse),
        const Duration(seconds: 1),
      );
    });
  });

  group('the client', () {
    test('sends no more than 60 browsing requests in two minutes, and holds '
        'the next without contact', () async {
      final service = await FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : exchanges.answer('servers-list'),
      );
      final time = FakeTime();
      final client = testClient(time: time);
      final filter = ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1);
      await service.run(() async {
        for (var request = 0; request < 60; request++) {
          expect((await client.browse(filter)).isAvailable, isTrue);
        }
        final held = await client.browse(filter);
        expect(held.problem, MultiplayerServiceProblem.rateLimited);
        expect(held.retryAfter, const Duration(minutes: 2));
        expect(held.unavailableMessage, contains('too often'));
        // Joins through a server's detail count as browsing too.
        final join = await client.resolve(
          JoinLinkTarget(
            SessionLink.server(
              'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3',
            ),
          ),
        );
        expect((join! as JoinFailed).code, JoinFailureCode.rateLimited);
      });
      expect(
        service.seen.where((seen) => seen.path == '/v1/servers'),
        hasLength(60),
      );
      expect(
        service.seen.where((seen) => seen.path.startsWith('/v1/servers/')),
        isEmpty,
      );
    });

    test('lets ten invite codes miss in two minutes, and holds the next '
        'without contact', () async {
      final service = await FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : CannedAnswer.error(404, 'not-found'),
      );
      final time = FakeTime();
      final client = testClient(time: time);
      const alphabet = InviteCode.alphabet;
      String codeNumber(int index) =>
          'AAAAAAAA${alphabet[index ~/ 32]}${alphabet[index % 32]}';
      await service.run(() async {
        for (var miss = 0; miss < 10; miss++) {
          final lookup = await client.lookUpInvite(codeNumber(miss));
          expect(lookup.problem, MultiplayerServiceProblem.notFound);
        }
        final held = await client.lookUpInvite(codeNumber(10));
        expect(held.problem, MultiplayerServiceProblem.rateLimited);
        time.advance(const Duration(minutes: 2));
        final again = await client.lookUpInvite(codeNumber(11));
        expect(again.problem, MultiplayerServiceProblem.notFound);
      });
      expect(
        service.seen.where((seen) => seen.path == '/v1/invites/resolve'),
        hasLength(11),
      );
    });

    test('gives an invite\'s slot back when it found its server', () async {
      final service = await FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : exchanges.answer('invite-resolve'),
      );
      final client = testClient();
      await service.run(() async {
        for (var hit = 0; hit < 15; hit++) {
          expect((await client.lookUpInvite('F80BH-RS9YW')).isFound, isTrue);
        }
      });
      expect(
        service.seen.where((seen) => seen.path == '/v1/invites/resolve'),
        hasLength(15),
      );
    });
  });
}
