part of '../local_launcher_repository.dart';

/// Private acceptance access retains the actual repository-created request.
extension AcceptanceLaunchAccess on LocalLauncherRepository {
  Future<AcceptanceIsolationAcknowledgement?> acceptanceAcknowledgement(
    LaunchResult result,
  ) {
    final request = _acceptanceRequests[result.requestId];
    final process = result.process;
    if (request == null || process == null || !result.processStarted) {
      throw StateError(
        'Acceptance requires an original owned process receipt.',
      );
    }
    request.context.verify();
    return LaunchStagingStore(
      request.context.gameRoot,
    ).readAcceptanceAcknowledgement(request, process);
  }

  Future<void> stopAcceptanceProcess(LaunchResult result) async {
    final request = _acceptanceRequests[result.requestId];
    final process = result.process;
    if (request == null ||
        process == null ||
        !_ownedLaunchProcesses.values.any(
          (owned) =>
              owned.pid == process.pid &&
              owned.nativeStartToken == process.nativeStartToken &&
              owned.startTimeUtc == process.startTimeUtc &&
              sameAcceptancePath(owned.executablePath, process.executablePath),
        )) {
      throw StateError('Acceptance refuses to stop an unowned process.');
    }
    final alive = await _gameProcessLiveness(process);
    if (alive == null) {
      throw StateError(
        'Acceptance process liveness is unknown; cleanup was not confirmed.',
      );
    }
    if (alive) await _gameProcessStopper(process);
    if (await _gameProcessLiveness(process) != false) {
      throw StateError('Acceptance process exit could not be confirmed.');
    }
    _ownedLaunchProcesses.removeWhere((_, value) => identical(value, process));
  }
}
