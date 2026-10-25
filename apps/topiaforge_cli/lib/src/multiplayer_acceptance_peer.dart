import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'live_acceptance_session.dart';
import 'multiplayer_acceptance_spec.dart';

/// The probe starts through the existing V4 isolation bootstrap. It owns the
/// multiplayer exercise after admission; this never bypasses the V5 guard.
Future<LiveAcceptanceSession> launchMultiplayerAcceptancePeer(
  String repositoryRoot,
  AcceptanceIsolationContext context,
  String challenge,
  String peer,
) async {
  context.verify();
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
    if (install == null) throw StateError('The isolated game is unavailable.');
    final profile = snapshot.profiles.singleWhere(
      (p) => p.id == snapshot.selectedProfileId,
    );
    final result = await repository.launch(
      install,
      profile,
      selectionOverride: LaunchSelection.target(
        LaunchRequest(targetId: '${MultiplayerAcceptanceSpec.packageId}.$peer'),
      ),
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
    return OwnedAcceptanceSession(
      context: context,
      process: process,
      acknowledgement: () => repository.acceptanceAcknowledgement(result),
      outcome: () => store.readOutcome(result.requestId!, session: false),
      processLiveness: isLaunchProcessAlive,
      stopAndConfirmExit: (receipt) {
        if (!identical(receipt, process)) {
          throw StateError('The original receipt is required.');
        }
        return repository.stopAcceptanceProcess(result);
      },
      dispose: repository.dispose,
    );
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
