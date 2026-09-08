import 'dart:async';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'live_acceptance_models.dart';

/// Acceptance owns a production launch receipt until verified process exit.
/// An isolation acknowledgement is distinct from the Running launch outcome.
abstract interface class LiveAcceptanceSession {
  Future<void> confirm(Duration timeout);
  Future<void> close();
  Map<String, Object?> get isolationEvidence;
}

typedef LiveAcceptanceSessionLauncher =
    Future<LiveAcceptanceSession> Function(
      LiveAcceptanceOptions options,
      AcceptanceIsolationContext context,
      String challenge,
    );

final class RepositoryAcceptanceSession {
  RepositoryAcceptanceSession._();

  static Future<LiveAcceptanceSession> launch(
    LiveAcceptanceOptions options,
    AcceptanceIsolationContext context,
    String challenge,
  ) async {
    context.verify();
    final repository = LocalLauncherRepository(
      dataRoot: context.launcherRoot,
      repositoryRoot: options.repositoryRoot,
      knownGamePath: context.gameRoot,
      acceptanceIsolation: context,
      acceptanceChallenge: challenge,
    );
    try {
      final snapshot = await repository.loadSnapshot();
      final install = await repository.detectKnownInstall();
      if (install == null) {
        throw StateError('The isolated game installation is unavailable.');
      }
      final profile = snapshot.profiles
          .where((item) => item.id == snapshot.selectedProfileId)
          .single;
      final result = await repository.launch(
        install,
        profile,
        selectionOverride: LaunchSelection.target(
          LaunchRequest(targetId: 'dev.topiaforge.sdk-acceptance.menu'),
        ),
      );
      if (!result.processStarted ||
          result.process == null ||
          result.requestId == null) {
        throw StateError(
          'Acceptance did not obtain an original game process receipt: ${result.message}',
        );
      }
      final process = result.process!;
      final store = LaunchStagingStore(context.gameRoot);
      return OwnedAcceptanceSession(
        context: context,
        process: process,
        acknowledgement: () => repository.acceptanceAcknowledgement(result),
        outcome: () => store.readOutcome(result.requestId!, session: false),
        processLiveness: isLaunchProcessAlive,
        stopAndConfirmExit: (receipt) {
          if (!identical(receipt, process)) {
            throw StateError(
              'Acceptance cleanup requires its original receipt.',
            );
          }
          return repository.stopAcceptanceProcess(result);
        },
        dispose: repository.dispose,
      );
    } on Object {
      await repository.dispose();
      rethrow;
    }
  }
}

/// Polling and cleanup shared by the production repository session and bounded
/// tests. Process operations always receive the captured original receipt;
/// callbacks never select or reopen a process by its PID.
final class OwnedAcceptanceSession implements LiveAcceptanceSession {
  OwnedAcceptanceSession({
    required AcceptanceIsolationContext context,
    required LaunchProcessIdentity process,
    required Future<AcceptanceIsolationAcknowledgement?> Function()
    acknowledgement,
    required Future<LaunchOutcome?> Function() outcome,
    required Future<bool?> Function(LaunchProcessIdentity) processLiveness,
    required Future<void> Function(LaunchProcessIdentity) stopAndConfirmExit,
    required Future<void> Function() dispose,
    DateTime Function()? clock,
    Future<void> Function(Duration)? delay,
  }) : _context = context,
       _process = process,
       _acknowledgement = acknowledgement,
       _outcome = outcome,
       _processLiveness = processLiveness,
       _stopAndConfirmExit = stopAndConfirmExit,
       _dispose = dispose,
       _clock = clock ?? DateTime.now,
       _delay = delay ?? Future<void>.delayed;

  final AcceptanceIsolationContext _context;
  final LaunchProcessIdentity _process;
  final Future<AcceptanceIsolationAcknowledgement?> Function() _acknowledgement;
  final Future<LaunchOutcome?> Function() _outcome;
  final Future<bool?> Function(LaunchProcessIdentity) _processLiveness;
  final Future<void> Function(LaunchProcessIdentity) _stopAndConfirmExit;
  final Future<void> Function() _dispose;
  final DateTime Function() _clock;
  final Future<void> Function(Duration) _delay;
  AcceptanceIsolationAcknowledgement? _ack;
  bool _exitConfirmed = false;
  Future<void>? _closing;

  @override
  Future<void> confirm(Duration timeout) async {
    final deadline = _clock().toUtc().add(timeout);
    while (_clock().toUtc().isBefore(deadline)) {
      _ack ??= await _acknowledgement();
      final outcome = await _outcome();
      if (outcome != null && outcome.status != 'succeeded') {
        throw StateError('The isolated runtime refused the acceptance launch.');
      }
      if (await _processLiveness(_process) != true) {
        throw StateError(
          'The owned acceptance process exited or became unverifiable; startup remains unconfirmed.',
        );
      }
      if (_ack != null &&
          outcome?.status == 'succeeded' &&
          outcome?.command == 'launch-target' &&
          outcome?.phase == 'running') {
        return;
      }
      await _delay(const Duration(milliseconds: 100));
    }
    throw StateError(
      'Acceptance isolation/session acknowledgement is unknown or unconfirmed.',
    );
  }

  @override
  Future<void> close() => _closing ??= _close();
  Future<void> _close() async {
    try {
      await _stopAndConfirmExit(_process);
      _exitConfirmed = true;
    } finally {
      await _dispose();
    }
  }

  @override
  Map<String, Object?> get isolationEvidence {
    if (_ack == null || !_exitConfirmed) {
      throw StateError(
        'Acceptance evidence requires verified isolation and owned process exit.',
      );
    }
    return {
      'kind': _context.kind,
      'provisioningRecordSha256': _context.recordSha256,
      'acknowledgementSha256': _ack!.sha256Digest,
      'acknowledgement': _ack!.document,
      'processExitConfirmed': true,
    };
  }
}
