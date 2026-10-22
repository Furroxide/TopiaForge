import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/join_descriptor_resolver.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';

/// A join's hold on LAN browsing, over fake sockets with time in the test's
/// hands: what a stop does while it stands, how browsing a hold had to start
/// ends, how long a download waits for its host's beacon, and which joins
/// may not ask their host for packages at all.
void main() {
  late LanRig rig;
  late LanDiscoveryClient client;

  setUp(() {
    rig = LanRig();
    client = rig.client;
    addTearDown(client.dispose);
  });

  group('while a hold stands', () {
    test('a plain stop waits, and runs once the hold is released', () async {
      await client.start();
      rig.beacon(hostAServerId, lanHost);
      final hold = await client.hold();
      final snapshot = await client.stop();
      expect(snapshot.status, LanDiscoveryStatus.listening);
      expect(client.isListening, isTrue);
      expect(await client.hearsServer(hostAServerId), isTrue);
      hold.release();
      await Future<void>.delayed(Duration.zero);
      expect(client.isListening, isFalse);
      expect(client.snapshot.status, LanDiscoveryStatus.stopped);
    });

    test('turning discovery off, or a port lost, stops at once', () async {
      for (final stop in <Future<LanBrowseSnapshot> Function()>[
        () => client.stop(disabled: true),
        () => client.stop(unavailable: LanDiscoveryProblem.portInUse),
      ]) {
        await client.start();
        final hold = await client.hold();
        await stop();
        expect(client.isListening, isFalse);
        hold.release();
        await Future<void>.delayed(Duration.zero);
        expect(client.isListening, isFalse);
      }
    });

    test('a start after the stop keeps browsing once released', () async {
      await client.start();
      final hold = await client.hold();
      await client.stop();
      await client.start();
      hold.release();
      await Future<void>.delayed(Duration.zero);
      expect(client.isListening, isTrue);
    });

    test('each of several holds keeps it until the last goes', () async {
      await client.start();
      final first = await client.hold();
      final second = await client.hold();
      await client.stop();
      first.release();
      first.release();
      await Future<void>.delayed(Duration.zero);
      expect(client.isListening, isTrue);
      second.release();
      await Future<void>.delayed(Duration.zero);
      expect(client.isListening, isFalse);
    });

    test('disposing stops it anyway', () async {
      await client.start();
      final hold = await client.hold();
      await client.dispose();
      expect(client.isListening, isFalse);
      hold.release();
    });
  });

  group('a hold on stopped browsing', () {
    test('starts it when the settings allow, and stops it after', () async {
      final hold = await client.hold(mayStart: () async => true);
      expect(client.isListening, isTrue);
      hold.release();
      await Future<void>.delayed(Duration.zero);
      expect(client.isListening, isFalse);
    });

    test('starts nothing when they do not, or when nothing is asked', () async {
      final refused = await client.hold(mayStart: () async => false);
      final unasked = await client.hold();
      final failing = await client.hold(
        mayStart: () async => throw StateError('settings unreadable'),
      );
      expect(client.isListening, isFalse);
      expect(rig.binder.sockets, isEmpty);
      for (final hold in [refused, unasked, failing]) {
        hold.release();
      }
    });

    test(
      'yields to discovery turned off while the settings were read',
      () async {
        final reading = Completer<bool>();
        final holding = client.hold(mayStart: () => reading.future);
        await client.stop(disabled: true);
        reading.complete(true);
        final hold = await holding;
        expect(client.isListening, isFalse);
        expect(rig.binder.sockets, isEmpty);
        hold.release();
      },
    );

    test('released before the settings were read starts nothing', () async {
      final reading = Completer<bool>();
      final holding = client.hold(mayStart: () => reading.future);
      // The hold's own release cannot come before it is handed out, but a
      // dispose can, and nothing may start after it.
      await client.dispose();
      reading.complete(true);
      await holding;
      expect(rig.binder.sockets, isEmpty);
    });
  });

  group('waiting for a beacon', () {
    test('ends when it arrives', () async {
      await client.start();
      final heard = client.hearsServer(hostAServerId);
      rig.beacon(hostAServerId, lanHost);
      expect(await heard, isTrue);
    });

    test('gives up at the first sweep after the expiry', () async {
      await client.start();
      var settled = false;
      final heard = client
          .hearsServer(hostAServerId)
          .whenComplete(() => settled = true);
      rig.clock.advance(const Duration(seconds: 5));
      rig.ticker.fire();
      await Future<void>.delayed(Duration.zero);
      expect(settled, isFalse);
      rig.clock.advance(const Duration(seconds: 1));
      rig.ticker.fire();
      expect(await heard, isFalse);
    });

    test('ends at a stop, a cancel, or when nothing listens', () async {
      expect(await client.hearsServer(hostAServerId), isFalse);
      await client.start();
      final stopped = client.hearsServer(hostAServerId);
      await client.stop();
      expect(await stopped, isFalse);
      await client.start();
      final cancellation = CancellationToken();
      final cancelled = client.hearsServer(
        hostAServerId,
        cancellation: cancellation,
      );
      cancellation.cancel();
      expect(await cancelled, isFalse);
    });

    test('a beacon for another server does not end it', () async {
      await client.start();
      var settled = false;
      final heard = client
          .hearsServer(hostAServerId)
          .whenComplete(() => settled = true);
      rig.beacon(hostBServerId, otherLanHost);
      await Future<void>.delayed(Duration.zero);
      expect(settled, isFalse);
      rig.beacon(hostAServerId, lanHost);
      expect(await heard, isTrue);
    });

    test('a download from a host heard beyond the local network is refused '
        'under relay-only, without a connection', () async {
      await client.start();
      rig.beacon(hostAServerId, publicHost);
      final request = await _downloadRequest(
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.lan,
        ),
      );
      final (result, attempts) = await _countingConnections(
        () => client.fetchPackage(
          request,
          'ab' * 32,
          File('${Directory.systemTemp.path}/never-written.tmp'),
        ),
      );
      expect(
        (result as JoinHostPackageFailed).problem,
        JoinHostPackageProblem.directNotAllowed,
      );
      expect(attempts, 0);
    });
  });

  group('the host packages of a join', () {
    late Directory root;

    setUp(() {
      root = Directory.systemTemp.createTempSync('tf-host-routes-');
      addTearDown(() => root.deleteSync(recursive: true));
    });

    JoinDescriptorResolver resolver() => JoinDescriptorResolver(
      descriptors: DescriptorClient(),
      store: MultiplayerStore('${root.path}/multiplayer'),
      lan: client,
    );

    test('come from nowhere for a join the service resolved, even when its '
        'server is heard on the local network', () async {
      await client.start();
      rig.beacon(hostAServerId, lanHost);
      expect(await client.hearsServer(hostAServerId), isTrue);
      final routes = resolver();
      for (final target in <JoinTarget>[
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.internet,
        ),
        JoinLinkTarget(SessionLink.server(hostAServerId)),
      ]) {
        final request = await _downloadRequest(
          target,
          privacy: MultiplayerSettings()
              .acknowledgePrivacyDisclosure()
              .privacyForSession(allowDirect: true),
        );
        final packages = routes.hostPackagesFor(request);
        final ((prepared, downloaded), attempts) = await _countingConnections(
          () async => (
            await packages.prepare(),
            await packages.download(
              request.hostDownloads.single,
              '${root.path}/never-written.tmp',
            ),
          ),
        );
        packages.release();
        expect(prepared, JoinHostPackageProblem.hostUnknown, reason: '$target');
        expect(
          (downloaded as JoinHostPackageFailed).problem,
          JoinHostPackageProblem.hostUnknown,
          reason: '$target',
        );
        expect(attempts, 0, reason: '$target');
      }
      expect(File('${root.path}/never-written.tmp').existsSync(), isFalse);
    });

    test('come from nowhere for a direct link whose descriptor this launcher '
        'never fetched', () async {
      final request = await _downloadRequest(
        JoinLinkTarget(
          SessionLink.direct(
            serverId: hostAServerId,
            host: '192.168.1.20',
            port: 27250,
          ),
        ),
      );
      final packages = resolver().hostPackagesFor(request);
      final ((prepared, downloaded), attempts) = await _countingConnections(
        () async => (
          await packages.prepare(),
          await packages.download(
            request.hostDownloads.single,
            '${root.path}/never-written.tmp',
          ),
        ),
      );
      packages.release();
      expect(prepared, JoinHostPackageProblem.unreachable);
      expect(
        (downloaded as JoinHostPackageFailed).problem,
        JoinHostPackageProblem.unreachable,
      );
      expect(attempts, 0);
      expect(File('${root.path}/never-written.tmp').existsSync(), isFalse);
    });
  });
}

/// A join of [target], relay-only unless [privacy] says otherwise, at the
/// download of a package only its host, host A, serves.
Future<JoinDownloadRequest> _downloadRequest(
  JoinTarget target, {
  SessionPrivacy privacy = SessionPrivacy.relayOnly,
}) async {
  final hosted = TestArchive('example.hosted', '1.0.0');
  final server = await verifiedServer([
    hosted.package,
  ], hostServesPackages: true);
  final plan = planJoin(server, {
    hosted.sha256Hex: JoinPackageResolution.hostOffered(),
  }, developerMode: true);
  final state = [
    if (target.resolvesThroughService) JoinTargetResolved(server.serverId),
    JoinDescriptorVerified(server),
    JoinPlanReady(plan),
    const JoinConsentGiven(hostServedAcknowledged: true),
  ].fold(JoinSession.start(target, privacy: privacy), JoinSession.reduce);
  return state.downloadRequest!;
}

/// Runs [body] where every connection it tries to open fails at once, and
/// counts the tries.
Future<(T, int)> _countingConnections<T>(Future<T> Function() body) async {
  var attempts = 0;
  final result = await IOOverrides.runZoned(
    body,
    socketStartConnect: (host, port, {sourceAddress, sourcePort = 0}) {
      attempts++;
      throw const SocketException('No connection may open.');
    },
  );
  return (result, attempts);
}
