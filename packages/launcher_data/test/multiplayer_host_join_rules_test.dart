import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_host_served_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_network_support.dart';

/// The rules a host-served download keeps, through the launcher's real data
/// layer: developer mode and the acknowledgement checked by the data layer
/// itself, the host contacted only as the descriptor fetch reached it,
/// relay-only honoured, LAN browsing held through the downloads and released
/// after, and nothing partial left behind.
void main() {
  final hosted = TestArchive(
    'example.hosted',
    '1.0.0',
    name: 'Hosted Tools',
    payload: noisyPayload(150 * 1024),
  );
  // No source vouches for a host-served package, so it is named by its id.
  const hostedName = 'example.hosted 1.0.0';

  /// Waits, briefly and without a fixed sleep, for [done].
  Future<void> until(bool Function() done) async {
    for (var round = 0; round < 5000 && !done(); round++) {
      await Future<void>.delayed(const Duration(milliseconds: 1));
    }
    expect(done(), isTrue, reason: 'waited too long');
  }

  void expectNoAddress(String text) {
    for (final address in addressTexts) {
      expect(text, isNot(contains(address)), reason: address);
    }
  }

  JoinFailure failureOf(JoinSessionState state) => state.outcome!.failure!;

  group('the data layer itself', () {
    test('takes nothing from a host without developer mode', () async {
      final sourced = TestArchive('example.sourced', '1.0.0');
      final host = await ServingHost.start(
        [hosted],
        lists: [hosted.package, sourced.package],
      );
      final join = await HostJoin.create(host);
      await join.hearHost();
      final file = sourced.writeTo(join.rig.root);
      final state = await join.downloading(
        resolutions: {
          sourced.sha256Hex: sourced.offeredAt(file.uri.toString()),
        },
      );
      // Turned off after the plan was made: the data layer reads it again.
      await join.rig.repository.setDeveloperMode(false);
      final ended = await join.drive(state);
      final failure = failureOf(ended);
      expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
      expect(failure.stage, JoinStage.downloading);
      expect(failure.detail, contains('developer mode'));
      expectNoAddress(failure.detail);
      expectNoAddress(join.launcherLog());
      expect(failure.detail, contains(hostedName));
      expect(failure.detail, contains('Nothing was downloaded.'));
      expect(host.packageRequests, 0);
      // Not even the registry archive was fetched first.
      expect(join.cacheFiles(), isEmpty);
    });

    test('never leaves consent without the acknowledgement', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      var state = JoinSession.start(
        join.listing,
        privacy: SessionPrivacy.relayOnly,
      );
      state = JoinSession.reduce(
        state,
        (await join.multiplayer.answerJoin(state))!,
      );
      final plan = const JoinPlanner().plan(
        server: state.descriptor!,
        resolutions: {hosted.sha256Hex: JoinPackageResolution.hostOffered()},
        environment: JoinEnvironment(
          gameBuild: '2545',
          runtimeVersion: '0.2.0',
          sdkVersion: '0.2.0',
          protocolVersion: TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
          developerMode: true,
        ),
        mayContactHost: state.mayContactHost,
      );
      state = [
        JoinPlanReady(plan),
        const JoinConsentGiven(),
      ].fold(state, JoinSession.reduce);
      expect(state.stage, JoinStage.awaitingConsent);
      expect(state.hostServedAcknowledged, isFalse);
      expect(
        await join.multiplayer.answerJoin(state, install: join.rig.install),
        isNull,
      );
      expect(host.packageRequests, 0);
    });

    test('never asks a host the service found: the join ends at checking, '
        'before consent, whatever its privacy', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      // The same server is heard on the local network, but this join found
      // it through the service, so where it is stays unknown.
      await join.hearHost();
      final verified = await const ServerDescriptorVerifier(
        Ed25519DescriptorSignatureVerifier(),
      ).verify(host.descriptor);
      final server = (verified as ServerDescriptorAccepted).descriptor;
      final direct = MultiplayerSettings()
          .acknowledgePrivacyDisclosure()
          .privacyForSession(allowDirect: true);
      JoinPlan planWith({required bool mayContactHost}) => planJoin(
        server,
        {hosted.sha256Hex: JoinPackageResolution.hostOffered()},
        developerMode: true,
        mayContactHost: mayContactHost,
      );
      // As a planner told the host is reachable plans, wrongly.
      final wrong = planWith(mayContactHost: true);
      expect(wrong.hasUnverifiedPackages, isTrue);
      for (final target in <JoinTarget>[
        JoinLinkTarget(SessionLink.server(join.serverId)),
        JoinListingTarget(
          serverId: join.serverId,
          source: ServerListingSource.internet,
        ),
      ]) {
        for (final privacy in [SessionPrivacy.relayOnly, direct]) {
          final start = JoinSession.start(target, privacy: privacy);
          final checking = [
            JoinTargetResolved(join.serverId),
            JoinDescriptorVerified(server),
          ].fold(start, JoinSession.reduce);
          expect(checking.stage, JoinStage.checking);
          // As the launcher plans.
          final planned = planWith(mayContactHost: checking.mayContactHost);
          expect(planned.hasUnverifiedPackages, isFalse);
          for (final plan in [planned, wrong]) {
            final reason = '$target, ${privacy.allowsDirect}';
            final ended = JoinSession.reduce(checking, JoinPlanReady(plan));
            final failure = failureOf(ended);
            expect(failure.code, JoinFailureCode.contentUnavailable);
            expect(failure.stage, JoinStage.checking, reason: reason);
            expect(failure.detail, contains('Only the host has $hostedName'));
            expect(
              failure.detail,
              contains('on your local network or by an address the host'),
            );
            expectNoAddress(failure.detail);
            // Nothing is left for the data layer to answer, and nothing to
            // fetch from the host.
            expect(ended.downloadRequest, isNull);
            final (event, attempts) = await noConnections(
              () =>
                  join.multiplayer.answerJoin(ended, install: join.rig.install),
            );
            expect(event, isNull);
            expect(attempts, 0, reason: reason);
          }
        }
      }
      expect(host.packageRequests, 0);
      expect(join.cacheFiles(), isEmpty);
    });
  });

  group('relay-only', () {
    test(
      'never contacts a host now heard from beyond the local network',
      () async {
        final host = await ServingHost.start([hosted]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        final state = await join.downloading();
        // Its beacons now come from beyond the local network.
        join.beacon(from: publicHost);
        final (ended, attempts) = await noConnections(() => join.drive(state));
        final failure = failureOf(ended);
        expect(failure.code, JoinFailureCode.directNotAllowed);
        expect(failure.stage, JoinStage.downloading);
        expect(failure.detail, contains('Hide my IP'));
        expect(failure.detail, contains(hostedName));
        expectNoAddress(failure.detail);
        expect(attempts, 0);
        expect(host.packageRequests, 0);
        expect(join.cacheFiles(), isEmpty);
        expectNoAddress(join.launcherLog());
      },
    );
  });

  group('LAN browsing', () {
    test(
      'stays on through the downloads when asked to stop, then stops',
      () async {
        final host = await ServingHost.start([hosted]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        final state = await join.downloading();
        final release = Completer<void>();
        addTearDown(() => release.isCompleted ? null : release.complete());
        host.answerPackages = (socket, archive) async {
          await release.future;
          await ServingHost.serveArchive(socket, archive!.bytes);
        };
        final driving = join.drive(state, until: JoinStage.installing);
        await until(() => host.packageRequests == 1);
        // The player leaves the browser: the stop waits for the downloads.
        await join.multiplayer.stopLanBrowsing();
        expect(join.lan.client.isListening, isTrue);
        release.complete();
        final installing = await driving;
        expect(installing.stage, JoinStage.installing);
        expect(join.lan.client.isListening, isFalse);
      },
    );

    test('a join whose browsing had stopped starts it, waits for the '
        "host's beacon, and stops it after", () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      await join.multiplayer.stopLanBrowsing();
      expect(join.lan.client.isListening, isFalse);
      final driving = join.drive(state, until: JoinStage.installing);
      await until(
        () =>
            join.lan.binder.sockets.length == 4 && join.lan.client.isListening,
      );
      join.beacon();
      final installing = await driving;
      expect(
        installing.stage,
        JoinStage.installing,
        reason: '${installing.outcome?.failure?.detail}',
      );
      expect(host.packageRequests, 1);
      expect(join.lan.client.isListening, isFalse);
    });

    test(
      'a host that stays silent once browsing starts again is not heard',
      () async {
        final host = await ServingHost.start([hosted]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        final state = await join.downloading();
        await join.multiplayer.stopLanBrowsing();
        final driving = join.drive(state);
        await until(() => join.lan.client.isListening);
        await Future<void>.delayed(Duration.zero);
        join.lan.clock.advance(const Duration(seconds: 6));
        join.lan.ticker.fire();
        final failure = failureOf(await driving);
        expect(failure.code, JoinFailureCode.unreachable);
        expect(failure.detail, contains('no longer heard'));
        expectNoAddress(failure.detail);
        expectNoAddress(join.launcherLog());
        expect(failure.detail, contains('Nothing was downloaded.'));
        expect(host.packageRequests, 0);
        expect(join.lan.client.isListening, isFalse);
      },
    );

    test('turned off by the player is not started again, and nothing is '
        'contacted', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      final load = await join.multiplayer.loadSettings();
      await join.multiplayer.saveSettings(
        load.settings.withLanDiscovery(false),
      );
      final sockets = join.lan.binder.sockets.length;
      final (ended, attempts) = await noConnections(() => join.drive(state));
      final failure = failureOf(ended);
      expect(failure.code, JoinFailureCode.unreachable);
      expect(failure.detail, contains('LAN discovery is on'));
      expectNoAddress(failure.detail);
      expect(attempts, 0);
      expect(join.lan.binder.sockets.length, sockets);
      expect(join.lan.client.isListening, isFalse);
    });
  });

  group('cancelling', () {
    test('mid-download closes the connection, keeps nothing partial and '
        'lets browsing stop', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      final hold = Completer<void>();
      addTearDown(() => hold.isCompleted ? null : hold.complete());
      host.answerPackages = (socket, archive) async {
        final bytes = archive!.bytes;
        socket
          ..add('TFD1 200 ${bytes.length}\n'.codeUnits)
          ..add(bytes.sublist(0, 70000));
        await socket.flush();
        await hold.future;
      };
      final disconnected = host.disconnected.first;
      final cancellation = CancellationToken();
      final driving = join.drive(state, cancellation: cancellation);
      // Part of the archive is on disk, in a temporary file.
      final cache = cachedArchive(join.rig.dataRoot, hosted).parent;
      await until(
        () =>
            cache.existsSync() &&
            cache.listSync().any(
              (entity) =>
                  entity is File &&
                  entity.path.endsWith('.tmp') &&
                  entity.lengthSync() == 70000,
            ),
      );
      await join.multiplayer.stopLanBrowsing();
      expect(join.lan.client.isListening, isTrue);
      cancellation.cancel();
      final stopped = await driving;
      // The stage answered nothing: the reducer took the cancel itself.
      expect(stopped.stage, JoinStage.downloading);
      expect(
        JoinSession.reduce(
          stopped,
          const JoinCancelRequested(),
        ).outcome!.wasCancelled,
        isTrue,
      );
      expect(join.cacheFiles(), isEmpty);
      expect(join.lan.client.isListening, isFalse);
      await disconnected.timeout(const Duration(seconds: 10));
    });

    test('while waiting for the beacon stops at once', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      await join.multiplayer.stopLanBrowsing();
      final cancellation = CancellationToken();
      final driving = join.drive(state, cancellation: cancellation);
      await until(() => join.lan.client.isListening);
      cancellation.cancel();
      expect((await driving).stage, JoinStage.downloading);
      expect(host.packageRequests, 0);
      expect(join.lan.client.isListening, isFalse);
    });

    test('before the stage contacts nothing', () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      final event = await join.multiplayer.answerJoin(
        state,
        install: join.rig.install,
        cancellation: CancellationToken()..cancel(),
      );
      expect(event, isNull);
      expect(host.packageRequests, 0);
      expect(join.cacheFiles(), isEmpty);
      expect(join.lan.client.isListening, isTrue);
    });
  });

  group('hostile answers end the join in plain words and keep nothing', () {
    Future<JoinFailure> answeredWith(PackageAnswer answer) async {
      final host = await ServingHost.start([hosted]);
      host.answerPackages = answer;
      final join = await HostJoin.create(host);
      await join.hearHost();
      final ended = await join.drive(await join.downloading());
      final failure = failureOf(ended);
      expect(failure.stage, JoinStage.downloading);
      expect(failure.detail, contains(hostedName));
      expectNoAddress(failure.detail);
      expectNoAddress(join.launcherLog());
      expect(join.cacheFiles(), isEmpty);
      return failure;
    }

    test('another archive than the pinned one', () async {
      final impostor = TestArchive(
        'example.hosted',
        '1.0.0',
        name: 'Hosted Tools',
        payload: 'not what the session pins',
      );
      final failure = await answeredWith(
        (socket, _) => ServingHost.serveArchive(socket, impostor.bytes),
      );
      expect(failure.code, JoinFailureCode.integrityFailed);
      expect(failure.detail, contains('not the one its session pins'));
    });

    test('a length shorter than the archive', () async {
      final failure = await answeredWith(
        (socket, archive) => LoopbackDescriptorHost.raw(socket, [
          ...'TFD1 200 1000\n'.codeUnits,
          ...archive!.bytes,
        ]),
      );
      expect(failure.code, JoinFailureCode.integrityFailed);
    });

    test('an archive of another mod than the descriptor claims', () async {
      final other = TestArchive('example.other', '1.0.0');
      final claimed = DescriptorPackage(
        id: 'example.hosted',
        version: '1.0.0',
        sha256: other.sha256Hex,
        role: DescriptorPackageRole.implicit,
      );
      final host = await ServingHost.start([other], lists: [claimed]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final failure = failureOf(await join.drive(await join.downloading()));
      expect(failure.code, JoinFailureCode.manifestMismatch);
      expect(failure.detail, contains('another mod or version'));
      expect(join.cacheFiles(), isEmpty);
    });

    for (final (about, answer, code, words)
        in <(String, PackageAnswer, JoinFailureCode, String)>[
          (
            'a host that no longer has it',
            (socket, _) =>
                LoopbackDescriptorHost.raw(socket, 'TFD1 404\n'.codeUnits),
            JoinFailureCode.downloadFailed,
            'no longer has',
          ),
          (
            'a busy host',
            (socket, _) =>
                LoopbackDescriptorHost.raw(socket, 'TFD1 503\n'.codeUnits),
            JoinFailureCode.downloadFailed,
            'too busy',
          ),
          (
            'a body cut short',
            (socket, archive) => LoopbackDescriptorHost.raw(socket, [
              ...'TFD1 200 ${archive!.bytes.length}\n'.codeUnits,
              ...archive.bytes.sublist(0, 1000),
            ]),
            JoinFailureCode.downloadFailed,
            'cut off',
          ),
          (
            'a length past the bound',
            (socket, _) => LoopbackDescriptorHost.raw(socket, [
              ...'TFD1 200 ${ModRegistryFormat.maxPackageBytes + 1}\n'
                  .codeUnits,
              ...List<int>.filled(1000, 7),
            ]),
            JoinFailureCode.downloadFailed,
            'larger than a mod may be',
          ),
          (
            'a status line too long',
            (socket, _) =>
                LoopbackDescriptorHost.raw(socket, List<int>.filled(100, 0x54)),
            JoinFailureCode.downloadFailed,
            'not one this launcher can read',
          ),
          (
            'a connection closed before any answer',
            (socket, _) => LoopbackDescriptorHost.raw(socket, const []),
            JoinFailureCode.downloadFailed,
            'not one this launcher can read',
          ),
        ]) {
      test(about, () async {
        final failure = await answeredWith(answer);
        expect(failure.code, code);
        expect(failure.detail, contains(words));
      });
    }
  });
}
