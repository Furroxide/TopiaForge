part of '../local_launcher_repository.dart';

/// Private acceptance access retains the actual repository-created request.
extension AcceptanceLaunchAccess on LocalLauncherRepository {
  Future<AcceptanceIsolationAcknowledgement?> acceptanceAcknowledgement(
    LaunchResult result,
  ) {
    final owned = _ownedAcceptanceReceipt(result);
    final request = _acceptanceRequests[result.requestId]!;
    request.context.verify();
    return LaunchStagingStore(
      request.context.gameRoot,
    ).readAcceptanceAcknowledgement(request, owned);
  }

  Future<void> stopAcceptanceProcess(LaunchResult result) async {
    final owned = _ownedAcceptanceReceipt(result);
    // Equivalent transported values may identify the receipt, but operations
    // and ownership retirement always use the actual creation receipt object.
    final alive = await _gameProcessLiveness(owned);
    if (alive == null) {
      throw StateError(
        'Acceptance process liveness is unknown; cleanup was not confirmed.',
      );
    }
    if (alive) await _gameProcessStopper(owned);
    if (await _gameProcessLiveness(owned) != false) {
      throw StateError('Acceptance process exit could not be confirmed.');
    }
    _ownedLaunchProcesses.removeWhere((_, value) => identical(value, owned));
    _acceptanceReceipts.remove(result.requestId);
    _acceptanceRequests.remove(result.requestId);
  }

  LaunchProcessIdentity _ownedAcceptanceReceipt(LaunchResult result) {
    final process = result.process;
    final owned = _acceptanceReceipts[result.requestId];
    if (!result.processStarted ||
        !_acceptanceRequests.containsKey(result.requestId) ||
        process == null ||
        owned == null ||
        !_ownedLaunchProcesses.values.any((value) => identical(value, owned)) ||
        owned.pid != process.pid ||
        owned.nativeStartToken != process.nativeStartToken ||
        owned.startTimeUtc != process.startTimeUtc ||
        !sameAcceptancePath(owned.executablePath, process.executablePath)) {
      throw StateError(
        'Acceptance requires its original owned process receipt.',
      );
    }
    return owned;
  }
}
