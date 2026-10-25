part of '../local_launcher_repository.dart';

/// How often the launcher looks for a join that a game it started handed
/// over. The game writes one when the player asks it to join in the launcher
/// (INSTALL & JOIN IN LAUNCHER, or Open launcher on its failure view).
const _handoffLookEvery = Duration(seconds: 1);

/// The joins games this launcher started hand over (`session-handoff`).
///
/// While anyone listens, the launcher looks once a second in the staging
/// folder of each game it started, under that game's own launch request, and
/// takes each handoff once ([LaunchStagingSessions.takeSessionHandoff]). A
/// look is one file check per game, and none while it owns no game. A game
/// started any other way has no request id, so it hands nothing over: it
/// opens a `topiaforge://join/` link instead.
extension _JoinHandoffWatch on LocalLauncherRepository {
  Stream<JoinHandoffArrival> _handoffStream() {
    final existing = _handoffs;
    if (existing != null) return existing.stream;
    final controller = StreamController<JoinHandoffArrival>.broadcast(
      onListen: () {
        if (_disposed) return;
        _handoffTimer ??= Timer.periodic(
          _handoffLookEvery,
          (_) => _lookForHandoffs(),
        );
      },
      onCancel: () {
        _handoffTimer?.cancel();
        _handoffTimer = null;
      },
    );
    _handoffs = controller;
    return controller.stream;
  }

  /// One look in every game's staging folder; a look still running skips
  /// this one. The close waits for a look as for any other work.
  void _lookForHandoffs() {
    if (_handoffLooking || _disposed || _ownedLaunchProcesses.isEmpty) return;
    _handoffLooking = true;
    _tracked(() async {
      try {
        for (final MapEntry(key: identity, value: process)
            in _ownedLaunchProcesses.entries.toList()) {
          final requestId = _ownedLaunchRequests[process];
          final install = _ownedLaunchInstalls[identity];
          if (requestId == null || install == null || _disposed) continue;
          final handoff = await LaunchStagingStore(
            install.path,
          ).takeSessionHandoff(requestId);
          if (handoff == null || _disposed) continue;
          await _appendLauncherLogBestEffort(
            'A game this launcher started handed over a join '
            '(${handoff.target.kind}).',
          );
          final controller = _handoffs;
          if (controller != null && !controller.isClosed) {
            controller.add(
              JoinHandoffArrival(install: install, handoff: handoff),
            );
          }
        }
      } finally {
        _handoffLooking = false;
      }
    }).ignore();
  }

  /// Stops looking, as the repository closes.
  Future<void> _closeHandoffs() async {
    _handoffTimer?.cancel();
    _handoffTimer = null;
    await _handoffs?.close();
  }
}
