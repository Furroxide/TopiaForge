import 'dart:async';
import 'dart:io';

import 'package:launcher_data/src/multiplayer/host_package_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_host_served_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';
import 'multiplayer_network_support.dart';
import 'secure_http_loopback.dart';

/// The launcher repository's own rules for what the host served: its gates
/// at download and install, called without the multiplayer facade, the
/// install of a host-served archive from the verified cache, and the
/// facade's deadline for a host that stalls.
void main() {
  final hosted = TestArchive('example.hosted', '1.0.0', name: 'Hosted Tools');
  final sandbox = TestArchive('example.sandbox', '1.2.0', name: 'Sandbox');

  /// A join of a host on the local network serving [hosted], whose plan
  /// also downloads [sandbox] from a source, consented to and downloading.
  Future<(VerifiedServerDescriptor, JoinPlan, JoinSessionState)> downloading({
    DescriptorPackage? claimed,
  }) async {
    final server = await verifiedServer([
      claimed ?? hosted.package,
      sandbox.package,
    ], hostServesPackages: true);
    final plan = planJoin(server, {
      (claimed ?? hosted.package).sha256: JoinPackageResolution.hostOffered(),
      sandbox.sha256Hex: sandbox.offeredAt(
        'https://packages.example/${sandbox.fileName}',
      ),
    }, developerMode: true);
    expect(plan.hasUnverifiedPackages, isTrue);
    final state = consentedJoin(server, plan, onLocalNetwork: true);
    expect(state.stage, JoinStage.downloading);
    expect(state.hostServedAcknowledged, isTrue);
    return (server, plan, state);
  }

  group('downloading', () {
    test('with no way to the host fetches nothing either', () async {
      final rig = await JoinRig.create();
      await rig.repository.setDeveloperMode(true);
      final (_, _, state) = await downloading();
      final overrides = CountingHttpOverrides();
      final event = await HttpOverrides.runWithHttpOverrides(
        () => rig.repository.downloadJoinContent(rig.install, state),
        overrides,
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
      expect(failure.detail, contains('no way to reach the host'));
      expect(failure.detail, contains('Nothing was downloaded.'));
      expect(overrides.clientsCreated, 0);
    });

    test(
      'settings that cannot be read never count as developer mode',
      () async {
        final rig = await JoinRig.create();
        await rig.repository.setDeveloperMode(true);
        File('${rig.dataRoot}/settings.json').writeAsStringSync('{not json');
        final (_, _, state) = await downloading();
        final event = await rig.repository.downloadJoinContent(
          rig.install,
          state,
        );
        expect((event as JoinFailed).detail, contains('developer mode'));
      },
    );
  });

  group('installing', () {
    Future<(JoinRig, JoinSessionState)> installing({
      DescriptorPackage? claimed,
      List<TestArchive>? cached,
    }) async {
      final rig = await JoinRig.create();
      await rig.repository.setDeveloperMode(true);
      final (_, _, state) = await downloading(claimed: claimed);
      rig.cache(cached ?? [hosted, sandbox]);
      return (rig, JoinSession.reduce(state, const JoinDownloadsCompleted()));
    }

    test('installs what the host served from the verified cache', () async {
      final (rig, state) = await installing();
      final event = await rig.repository.installJoinContent(rig.install, state);
      expect(event, isA<JoinInstallCompleted>());
      expect(
        packageFolder(rig.game, 'example.hosted', '1.0.0').existsSync(),
        isTrue,
      );
    });

    test('installs nothing once developer mode is off', () async {
      final (rig, state) = await installing();
      await rig.repository.setDeveloperMode(false);
      final before = await rig.everything([hosted, sandbox]);
      final event = await rig.repository.installJoinContent(rig.install, state);
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(failure.detail, contains('developer mode'));
      expect(failure.detail, contains('example.hosted 1.0.0'));
      expect(failure.detail, contains('Nothing was installed.'));
      expect(await rig.everything([hosted, sandbox]), before);
    });

    test(
      'refuses an archive that is not the mod the descriptor claims',
      () async {
        final other = TestArchive('example.other', '1.0.0');
        final claimed = DescriptorPackage(
          id: 'example.hosted',
          version: '1.0.0',
          sha256: other.sha256Hex,
          role: DescriptorPackageRole.implicit,
        );
        final (rig, state) = await installing(
          claimed: claimed,
          cached: [other, sandbox],
        );
        final before = await rig.everything([hosted, other, sandbox]);
        final event = await rig.repository.installJoinContent(
          rig.install,
          state,
        );
        final failure = event as JoinFailed;
        expect(failure.code, JoinFailureCode.installFailed);
        expect(failure.detail, contains('another mod or version'));
        expect(await rig.everything([hosted, other, sandbox]), before);
      },
    );

    test('refuses a host-served archive the cache no longer holds', () async {
      final (rig, state) = await installing(cached: [sandbox]);
      final event = await rig.repository.installJoinContent(rig.install, state);
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.detail, contains('missing or damaged'));
      expect(
        packageFolder(rig.game, 'example.sandbox', '1.2.0').existsSync(),
        isFalse,
      );
    });
  });

  test('developer mode turned off while the host serves one package asks it '
      'for no more', () async {
    final first = TestArchive('example.alpha', '1.0.0');
    final second = TestArchive('example.beta', '1.0.0');
    final host = await ServingHost.start([first, second]);
    final join = await HostJoin.create(host);
    await join.hearHost();
    final state = await join.downloading();
    expect(state.downloadRequest!.hostDownloads, hasLength(2));
    host.answerPackages = (socket, archive) async {
      // The player turns developer mode off during the first download.
      await join.rig.repository.setDeveloperMode(false);
      await ServingHost.serveArchive(socket, archive!.bytes);
    };
    final failure = (await join.drive(state)).outcome!.failure!;
    expect(host.requests.where((line) => line.contains('package')), [
      'TFD1 package ${first.sha256Hex}\n',
    ]);
    expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
    expect(failure.stage, JoinStage.downloading);
    expect(failure.detail, contains('example.beta 1.0.0'));
    expect(failure.detail, contains('developer mode, which is off'));
    for (final address in addressTexts) {
      expect(failure.detail, isNot(contains(address)));
      expect(join.launcherLog(), isNot(contains(address)));
    }
  });

  test('developer mode turned off, or its settings unreadable, while the '
      "join waits for the host's beacon asks the host for nothing", () async {
    for (final unreadable in [false, true]) {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final state = await join.downloading();
      // Browsing stopped, so once the stage's own checks pass it starts
      // browsing again and waits for the host's beacon.
      await join.multiplayer.stopLanBrowsing();
      final driving = join.drive(state);
      bool waiting() =>
          join.lan.binder.sockets.length == 4 && join.lan.client.isListening;
      for (var round = 0; round < 5000 && !waiting(); round++) {
        await Future<void>.delayed(const Duration(milliseconds: 1));
      }
      expect(waiting(), isTrue);
      if (unreadable) {
        File('${join.rig.dataRoot}/settings.json').writeAsStringSync('{not');
      } else {
        await join.rig.repository.setDeveloperMode(false);
      }
      join.beacon();
      final failure = (await driving).outcome!.failure!;
      final reason = unreadable ? 'unreadable' : 'off';
      expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
      expect(failure.stage, JoinStage.downloading, reason: reason);
      expect(failure.detail, contains('example.hosted 1.0.0'));
      expect(failure.detail, contains('developer mode, which is off'));
      expect(host.packageRequests, 0, reason: reason);
      expect(join.cacheFiles(), isEmpty, reason: reason);
      expect(join.lan.client.isListening, isFalse, reason: reason);
      for (final address in addressTexts) {
        expect(failure.detail, isNot(contains(address)));
        expect(join.launcherLog(), isNot(contains(address)));
      }
    }
  });

  test(
    'a host that stalls mid-archive is given up on, keeping nothing',
    () async {
      final big = TestArchive(
        'example.hosted',
        '1.0.0',
        payload: noisyPayload(150 * 1024),
      );
      final timers = ManualTimers();
      final host = await ServingHost.start([big]);
      final hold = Completer<void>();
      addTearDown(() => hold.isCompleted ? null : hold.complete());
      host.answerPackages = (socket, archive) async {
        socket
          ..add('TFD1 200 ${archive!.bytes.length}\n'.codeUnits)
          ..add(archive.bytes.sublist(0, 70000));
        await socket.flush();
        await hold.future;
      };
      final join = await HostJoin.create(
        host,
        packages: HostPackageClient(timer: timers.call),
      );
      await join.hearHost();
      final state = await join.downloading();
      final driving = join.drive(state);
      final cache = cachedArchive(join.rig.dataRoot, big).parent;
      bool stallArmed() => timers.timers.any(
        (timer) => timer.isActive && timer.delay == const Duration(seconds: 15),
      );
      bool partial() =>
          cache.existsSync() &&
          cache.listSync().any(
            (entity) => entity is File && entity.lengthSync() == 70000,
          );
      // Part of the archive is on disk, and the host has gone quiet.
      for (var round = 0; round < 5000; round++) {
        if (partial() && stallArmed()) break;
        await Future<void>.delayed(const Duration(milliseconds: 1));
      }
      timers.timers
          .lastWhere(
            (timer) =>
                timer.isActive && timer.delay == const Duration(seconds: 15),
          )
          .fire();
      final failure = (await driving).outcome!.failure!;
      expect(failure.code, JoinFailureCode.unreachable);
      expect(failure.detail, contains('stopped sending'));
      expect(failure.detail, contains('example.hosted 1.0.0'));
      expect(join.cacheFiles(), isEmpty);
    },
  );
}
