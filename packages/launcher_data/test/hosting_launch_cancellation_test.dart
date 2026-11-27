import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_rig.dart';

void main() {
  for (final cancelAt in [0, 1, 2]) {
    test(
      'host cancellation at probe $cancelAt never creates a process',
      () async {
        final token = CancellationToken();
        var probes = 0, creates = 0;
        var armed = false;
        final rig = await JoinRig.create(
          withRuntime: true,
          probe: (_) async {
            probes++;
            if (armed && probes == cancelAt) token.cancel();
            return false;
          },
          creator: (_) async {
            creates++;
            throw StateError('must not create');
          },
        );
        final profile = (await rig.repository.loadSnapshot()).profiles.first;
        probes = 0;
        armed = true;
        if (cancelAt == 0) token.cancel();
        final request = LaunchSessionRequest.host(
          intentId: 'host-cancel',
          settings: MultiplayerSettings().acknowledgePrivacyDisclosure(),
          privacy: SessionPrivacy.relayOnly,
          host: LaunchHostOptions(name: 'Evening'),
        );
        final result = await rig.repository.launchSession(
          rig.install,
          profile,
          request,
          cancellation: token,
        );
        expect(result.processStarted, isFalse);
        expect(token.isCancelled, isTrue);
        expect(creates, 0);
        expect(
          rig.game
              .listSync(recursive: true)
              .whereType<File>()
              .where(
                (file) =>
                    file.uri.pathSegments.last.startsWith('launch-profile-'),
              ),
          isEmpty,
        );
      },
    );
  }
  test(
    'host admission refuses packages different from the reviewed digest',
    () async {
      var creates = 0;
      final rig = await JoinRig.create(
        withRuntime: true,
        creator: (_) async {
          creates++;
          throw StateError('must not create');
        },
      );
      final profile = (await rig.repository.loadSnapshot()).profiles.first;
      final request = LaunchSessionRequest.host(
        intentId: 'host-digest',
        settings: MultiplayerSettings().acknowledgePrivacyDisclosure(),
        privacy: SessionPrivacy.relayOnly,
        host: LaunchHostOptions(name: 'Evening'),
      );
      final result = await rig.repository.launchSession(
        rig.install,
        profile,
        request,
        expectedPackageDigest: 'different',
      );
      expect(result.processStarted, isFalse);
      expect(result.message, contains('reviewed host packages changed'));
      expect(creates, 0);
    },
  );
}
