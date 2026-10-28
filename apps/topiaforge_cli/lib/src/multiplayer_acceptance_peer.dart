import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'live_acceptance_session.dart';
import 'multiplayer_acceptance_spec.dart';

/// One pair owns a port and the descriptor verified after its host confirms.
/// No address is put in probe config, SDK state or evidence output.
final class MultiplayerAcceptancePair {
  int? _port;
  LaunchSessionTarget? _target;

  Future<LiveAcceptanceSession> launch(
    String repositoryRoot,
    AcceptanceIsolationContext context,
    String challenge,
    String peer,
  ) async {
    context.verify();
    if (peer != 'host' && peer != 'guest') {
      throw StateError('Unknown acceptance peer.');
    }
    if (peer == 'host') {
      if (_port != null) {
        throw StateError('This acceptance pair already started.');
      }
      final reservation = await ServerSocket.bind(
        InternetAddress.loopbackIPv4,
        0,
      );
      _port = reservation.port;
      await reservation.close();
    } else if (_target == null) {
      throw StateError(
        'Confirm the acceptance host before launching its guest.',
      );
    }
    final repository = LocalLauncherRepository(
      dataRoot: context.launcherRoot,
      repositoryRoot: repositoryRoot,
      knownGamePath: context.gameRoot,
      acceptanceIsolation: context,
      acceptanceChallenge: challenge,
    );
    LaunchResult? owned;
    try {
      final snapshot = await repository.loadSnapshot();
      final install = await repository.detectKnownInstall();
      if (install == null) {
        throw StateError('The isolated game is unavailable.');
      }
      final selected = snapshot.profiles.singleWhere(
        (p) => p.id == snapshot.selectedProfileId,
      );
      final profile = selected.copyWith(
        inheritManagerModState: false,
        enabledMods: snapshot.installedMods
            .where((mod) => mod.enabled)
            .map((mod) => mod.id)
            .toSet(),
        launchSettings: const LaunchSettings(),
      );
      final saved = (await repository.saveProfiles([
        profile,
      ], profile.id)).single;
      final settings = MultiplayerSettings(
        displayName: 'Acceptance $peer',
        upnpEnabled: false,
      );
      final session = peer == 'host'
          ? LaunchSessionRequest.host(
              intentId: '$challenge-host',
              settings: settings,
              privacy: settings.privacyForSession(allowDirect: false),
              port: _port!,
              host: LaunchHostOptions(name: 'Acceptance pair', maxPlayers: 2),
            )
          : LaunchSessionRequest.join(
              intentId: '$challenge-guest',
              settings: settings,
              privacy: settings.privacyForSession(allowDirect: false),
              target: _target!,
            );
      final result = await repository.launchSession(
        install,
        saved,
        session,
        selectionOverride: peer == 'host'
            ? LaunchSelection.target(
                LaunchRequest(
                  targetId: '${MultiplayerAcceptanceSpec.packageId}.session',
                ),
              )
            : null,
      );
      owned = result;
      if (!result.processStarted ||
          result.process == null ||
          result.requestId == null) {
        throw StateError(
          'The multiplayer probe did not obtain an owned launch receipt.',
        );
      }
      final process = result.process!;
      final store = LaunchStagingStore(context.gameRoot);
      LaunchProgress? latest;
      final inner = OwnedAcceptanceSession(
        context: context,
        process: process,
        acknowledgement: () => repository.acceptanceAcknowledgement(result),
        outcome: () => store.readOutcome(result.requestId!, session: false),
        expectedSession: session,
        progress: () async =>
            latest = await store.readProgress(result.requestId!),
        processLiveness: isLaunchProcessAlive,
        stopAndConfirmExit: (receipt) {
          if (!identical(receipt, process)) {
            throw StateError('The original receipt is required.');
          }
          return repository.stopAcceptanceProcess(result);
        },
        dispose: repository.dispose,
      );
      return _PeerSession(inner, () async {
        if (peer != 'host') return;
        context.verify();
        final serverId = latest?.session?.serverId;
        if (serverId == null || await isLaunchProcessAlive(process) != true) {
          throw StateError('The confirmed host identity is unavailable.');
        }
        _target = await verifyAcceptanceHostTarget(
          context,
          port: _port!,
          serverId: serverId,
        );
      });
    } on Object {
      try {
        if (owned?.processStarted == true && owned?.process != null) {
          await repository.stopAcceptanceProcess(owned!);
        }
      } finally {
        await repository.dispose();
      }
      rethrow;
    }
  }
}

final class _PeerSession implements LiveAcceptanceSession {
  _PeerSession(this.inner, this.confirmed);
  final LiveAcceptanceSession inner;
  final Future<void> Function() confirmed;
  @override
  Future<void> confirm(Duration timeout) async {
    await inner.confirm(timeout);
    await confirmed();
  }

  @override
  Future<void> verifyAlive() => inner.verifyAlive();

  @override
  Future<void> close() => inner.close();
  @override
  Map<String, Object?> get isolationEvidence => inner.isolationEvidence;
}
