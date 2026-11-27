import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/control_plane_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';
import 'multiplayer_network_support.dart';

/// Joins and browsing through the service, end to end against a fake
/// service on 127.0.0.1, through the launcher's multiplayer facade where a
/// later slice will call them.
void main() {
  final exchanges = RecordedExchanges.load();
  final invite = JoinLinkTarget(SessionLink.invite('F80BH-RS9YW'));
  final serverLink = JoinLinkTarget(SessionLink.server(hostAServerId));
  final listing = JoinListingTarget(
    serverId: hostAServerId,
    source: ServerListingSource.internet,
  );
  final filter = ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1);
  late Directory root;

  setUp(() => root = Directory.systemTemp.createTempSync('tf-mp-service-'));
  tearDown(() => root.deleteSync(recursive: true));

  /// The recorded service: its status, the server list, the server's
  /// detail and the invite's resolution.
  Future<FakeControlPlane> recorded({CannedAnswer? status}) =>
      FakeControlPlane.start(
        (request) => switch (request.path) {
          '/v1/status' => status ?? exchanges.answer('status'),
          '/v1/servers' => exchanges.answer('servers-list'),
          '/v1/invites/resolve' => exchanges.answer('invite-resolve'),
          _ => exchanges.answer('server-detail'),
        },
      );

  List<String> paths(FakeControlPlane service) => [
    for (final seen in service.seen) seen.path,
  ];

  /// Runs a join of [target] through [answer] from its first stage until
  /// the launcher's part ends: checking, or a failure.
  Future<JoinSessionState> join(
    JoinTarget target,
    Future<JoinEvent?> Function(JoinSessionState state) answer,
  ) async {
    var state = JoinSession.start(target, privacy: SessionPrivacy.relayOnly);
    while (state.stage == JoinStage.resolving ||
        state.stage == JoinStage.fetchingDescriptor) {
      state = JoinSession.reduce(state, (await answer(state))!);
    }
    return state;
  }

  test('a build that names a service talks to it through the facade', () async {
    final service = await recorded();
    final repo = LocalMultiplayerRepository(
      dataRoot: root.path,
      service: MultiplayerServiceConfiguration.parse('https://play.example'),
    );
    addTearDown(repo.dispose);
    expect(repo.serviceStatus, MultiplayerServiceStatus.configured);
    await service.run(() async {
      final status = await repo.readServiceStatus();
      expect(status.features!.relayAvailable, isTrue);
      final page = await repo.browseInternet(filter);
      expect(page.listings.single.serverId, hostAServerId);
      final server = await repo.fetchInternetServer(hostAServerId);
      expect(server.server!.descriptor.serverId, hostAServerId);
      for (final target in [invite, serverLink, listing]) {
        final state = await join(target, repo.answerJoin);
        expect(state.stage, JoinStage.checking, reason: '$target');
        expect(state.descriptor!.serverId, hostAServerId);
      }
    });
    expect(service.clientsCreated, greaterThan(0));
  });

  test('what resolving fetched answers the descriptor stage once; a re-plan '
      'asks the service again', () async {
    for (final (target, path) in [
      (invite, '/v1/invites/resolve'),
      (serverLink, '/v1/servers/$hostAServerId'),
    ]) {
      final service = await recorded();
      final client = testClient();
      var state = JoinSession.start(target, privacy: SessionPrivacy.relayOnly);
      state = JoinSession.reduce(
        state,
        (await service.run(() => client.resolve(target)))!,
      );
      final request = state.descriptorRequest!;
      expect(
        await service.run(() => client.fetchDescriptor(request)),
        isA<JoinDescriptorVerified>(),
      );
      expect(paths(service).where((seen) => seen == path), hasLength(1));
      // The admission changed and the join fetches the descriptor again.
      expect(
        await service.run(() => client.fetchDescriptor(request)),
        isA<JoinDescriptorVerified>(),
      );
      expect(paths(service).where((seen) => seen == path), hasLength(2));
    }
  });

  test('a re-plan that finds the invite or the server gone ends the join '
      'while fetching the descriptor', () async {
    for (final (target, code) in [
      (invite, JoinFailureCode.inviteExpired),
      (serverLink, JoinFailureCode.serverNotListed),
      (listing, JoinFailureCode.serverNotListed),
    ]) {
      var gone = false;
      final service = await FakeControlPlane.start(
        (request) => switch (request.path) {
          '/v1/status' => exchanges.answer('status'),
          _ when gone => CannedAnswer.error(404, 'not-found'),
          '/v1/invites/resolve' => exchanges.answer('invite-resolve'),
          _ => exchanges.answer('server-detail'),
        },
      );
      final client = testClient();
      var state = JoinSession.start(target, privacy: SessionPrivacy.relayOnly);
      state = JoinSession.reduce(
        state,
        (await service.run(() => client.resolve(target)))!,
      );
      final request = state.descriptorRequest!;
      await service.run(() => client.fetchDescriptor(request));
      gone = true;
      final event = await service.run(() => client.fetchDescriptor(request));
      expect((event! as JoinFailed).code, code, reason: '$target');
      final failure = JoinSession.reduce(state, event).outcome!.failure!;
      expect(failure.code, code);
      expect(failure.stage, JoinStage.fetchingDescriptor);
    }
  });

  test('what resolving fetched is used only for the same join, and only '
      'for a while', () async {
    final service = await recorded();
    final time = FakeTime();
    final client = testClient(time: time);
    final state = JoinSession.reduce(
      JoinSession.start(serverLink, privacy: SessionPrivacy.relayOnly),
      (await service.run(() => client.resolve(serverLink)))!,
    );
    // Another join of an equal link does not take this one's answer.
    final other = JoinSession.reduce(
      JoinSession.start(
        JoinLinkTarget(SessionLink.server(hostAServerId)),
        privacy: SessionPrivacy.relayOnly,
      ),
      JoinTargetResolved(hostAServerId),
    );
    await service.run(() => client.fetchDescriptor(other.descriptorRequest!));
    expect(
      paths(service).where((path) => path.startsWith('/v1/servers/')),
      hasLength(2),
    );
    time.advance(
      HttpsControlPlaneClient.resolvedMaxAge + const Duration(seconds: 1),
    );
    await service.run(() => client.fetchDescriptor(state.descriptorRequest!));
    expect(
      paths(service).where((path) => path.startsWith('/v1/servers/')),
      hasLength(3),
    );
  });

  group('the status first', () {
    test('ends a join while Internet joining or invites are off, before '
        'anything else is asked', () async {
      for (final (status, target, ends) in [
        (statusAnswer(signaling: false), serverLink, true),
        (statusAnswer(signaling: false), invite, true),
        (statusAnswer(invites: false), invite, true),
        (statusAnswer(invites: false), serverLink, false),
        (statusAnswer(relay: false, killSwitch: true), invite, false),
      ]) {
        final service = await recorded(status: status);
        final state = await join(
          target,
          (state) => service.run(
            () => state.stage == JoinStage.resolving
                ? testClient().resolve(target)
                : testClient().fetchDescriptor(state.descriptorRequest!),
          ),
        );
        if (ends) {
          final failure = state.outcome!.failure!;
          expect(failure.code, JoinFailureCode.serviceUnavailable);
          expect(failure.stage, JoinStage.resolving);
          expect(failure.detail, contains('turned off'));
          expect(paths(service), ['/v1/status']);
        } else {
          expect(state.stage, JoinStage.checking);
        }
      }
    });

    test('is read once for joins close together, and anew once an operator '
        'turned something off', () async {
      var disabled = false;
      final service = await FakeControlPlane.start(
        (request) => switch (request.path) {
          '/v1/status' => exchanges.answer('status'),
          _ when disabled => CannedAnswer.error(503, 'feature-disabled'),
          _ => exchanges.answer('server-detail'),
        },
      );
      final time = FakeTime();
      final client = testClient(time: time);
      int statusReads() =>
          paths(service).where((path) => path == '/v1/status').length;
      await service.run(() => client.resolve(serverLink));
      await service.run(() => client.resolve(serverLink));
      expect(statusReads(), 1);
      time.advance(HttpsControlPlaneClient.statusMaxAge);
      await service.run(() => client.resolve(serverLink));
      expect(statusReads(), 2);
      disabled = true;
      final failed =
          await service.run(() => client.resolve(serverLink)) as JoinFailed;
      expect(failed.code, JoinFailureCode.serviceUnavailable);
      expect(failed.detail, contains('turned this off'));
      await service.run(() => client.resolve(serverLink));
      expect(statusReads(), 3);
    });

    test('degrades: without the relay only direct and LAN sessions, with the '
        'reason, and each feature that is off', () async {
      for (final (status, reason, notice) in [
        (statusAnswer(relay: false), RelayOffReason.turnedOff, 'turned off'),
        (
          statusAnswer(relay: false, killSwitch: true),
          RelayOffReason.killSwitch,
          'paused',
        ),
      ]) {
        final service = await recorded(status: status);
        final check = await service.run(() => testClient().readStatus());
        final features = check.features!;
        expect(features.relayAvailable, isFalse);
        expect(features.paths, MultiplayerServicePaths.directAndLanOnly);
        expect(features.relayOffReason, reason);
        expect(features.featuresOff, {MultiplayerServiceFeature.relay});
        expect(features.notices.single, contains(notice));
        expect(
          features.notices.single,
          contains('only direct and LAN sessions are available'),
        );
      }
      final service = await recorded(
        status: statusAnswer(hosting: false, invites: false, signaling: false),
      );
      final features = (await service.run(
        () => testClient().readStatus(),
      )).features!;
      expect(features.featuresOff, {
        MultiplayerServiceFeature.hosting,
        MultiplayerServiceFeature.invites,
        MultiplayerServiceFeature.signaling,
      });
      expect(features.relayAvailable, isTrue);
      expect(features.notices, hasLength(3));
    });
  });

  group('cancellation', () {
    test(
      'mid-request ends every call at once and sends nothing more',
      () async {
        final release = Completer<void>();
        addTearDown(() => release.isCompleted ? null : release.complete());
        final service = await FakeControlPlane.start(
          (request) => request.path == '/v1/status'
              ? exchanges.answer('status')
              : CannedAnswer(
                  200,
                  bytes: exchanges.answer('server-detail').bytes,
                  hold: release.future,
                ),
        );
        final client = testClient();
        Future<void> cancelOnceSent(
          int requests,
          CancellationToken token,
        ) async {
          await eventually(() => service.seen.length >= requests);
          token.cancel();
        }

        final resolving = CancellationToken();
        final resolved = service.run(
          () => client.resolve(serverLink, cancellation: resolving),
        );
        await cancelOnceSent(2, resolving);
        expect(await resolved, isNull);

        final browsing = CancellationToken();
        final page = service.run(
          () => client.browse(filter, cancellation: browsing),
        );
        await cancelOnceSent(3, browsing);
        await expectLater(page, throwsA(isA<OperationCancelledException>()));

        final looking = CancellationToken();
        final lookup = service.run(
          () => client.fetchServer(hostAServerId, cancellation: looking),
        );
        await cancelOnceSent(4, looking);
        await expectLater(lookup, throwsA(isA<OperationCancelledException>()));
        expect(service.seen, hasLength(4));
      },
    );

    test('during a backoff ends the call without sending again', () async {
      final service = await FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : CannedAnswer.error(500, 'internal'),
      );
      final timers = ManualTimers();
      final client = HttpsControlPlaneClient(
        testOrigin,
        clock: FakeTime().call,
        timer: timers.call,
        networkPresence: () async => true,
      );
      final cancellation = CancellationToken();
      final resolved = service.run(
        () => client.resolve(serverLink, cancellation: cancellation),
      );
      await eventually(() => timers.timers.isNotEmpty);
      expect(timers.timers.single.delay, const Duration(seconds: 1));
      cancellation.cancel();
      expect(await resolved, isNull);
      expect(timers.timers.single.isActive, isFalse);
      expect(
        service.seen.where((seen) => seen.path != '/v1/status'),
        hasLength(1),
      );
    });

    test('before a call contacts nothing', () async {
      final service = await recorded();
      final cancelled = CancellationToken()..cancel();
      final client = testClient();
      await service.run(() async {
        expect(await client.resolve(invite, cancellation: cancelled), isNull);
        expect(
          await client.fetchDescriptor(
            JoinSession.reduce(
              JoinSession.start(serverLink, privacy: SessionPrivacy.relayOnly),
              JoinTargetResolved(hostAServerId),
            ).descriptorRequest!,
            cancellation: cancelled,
          ),
          isNull,
        );
        await expectLater(
          client.readStatus(cancellation: cancelled),
          throwsA(isA<OperationCancelledException>()),
        );
        await expectLater(
          client.browse(filter, cancellation: cancelled),
          throwsA(isA<OperationCancelledException>()),
        );
      });
      expect(service.clientsCreated, 0);
    });
  });

  test('only targets the service resolves go to it', () {
    final client = testClient();
    final typed = JoinAddressTarget(host: '192.168.1.20', port: 27250);
    expect(() => client.resolve(typed), throwsArgumentError);
    expect(
      () => client.browse(filter, after: 'not a server id'),
      throwsArgumentError,
    );
    expect(() => client.fetchServer('ABC'), throwsArgumentError);
  });
}
