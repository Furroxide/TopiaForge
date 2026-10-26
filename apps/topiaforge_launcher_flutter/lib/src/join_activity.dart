import 'package:launcher_domain/launcher_domain.dart';

/// The join events [activity], the game's records of a join, mean for
/// [join], in the order the reducer takes them.
///
/// The join waits at launching until the game shows it took the join: its
/// session has left `pending`, it reports the join's own phases, or it
/// settled the launch. A launch the game settled as failed before its session
/// began, with no multiplayer failure of its own, is a start that failed.
/// From there the session's state moves the join through connecting and
/// synchronizing into the session, a multiplayer failure ends it where it
/// stands, the session's end ends the session, and a process that exited
/// ends whatever is left. What the game wrote is shown without addresses.
List<JoinEvent> joinEventsFor(JoinSessionState join, LaunchActivity activity) {
  final events = <JoinEvent>[];
  final launch = activity.launchOutcome;
  final terminal = activity.sessionOutcome;
  final progress = activity.progress;
  final session = progress?.session?.state;
  final multiplayer = launch?.multiplayer;
  final failed = launch?.status == 'failed';
  final startFailed = failed && multiplayer?.failure == null;
  var reached = _levelOf(join.stage);
  final current = reached;
  void advanceTo(int level) {
    if (reached < 1 && level >= 1) events.add(const JoinGameStarted());
    if (reached < 2 && level >= 2) events.add(const JoinConnected());
    if (reached < 3 && level >= 3) events.add(const JoinSynchronized());
    if (level > reached) reached = level;
  }

  if (current < 0) return events;
  final inSession =
      session == LaunchSessionState.inSession ||
      launch?.status == 'succeeded' ||
      terminal?.multiplayer?.endReason != null;
  final synchronizing =
      inSession ||
      session == LaunchSessionState.synchronizing ||
      progress?.phase == 'synchronizing';
  final started =
      synchronizing ||
      (session != null && session != LaunchSessionState.pending) ||
      progress?.phase == 'connecting' ||
      (launch != null && !startFailed);
  advanceTo(
    inSession
        ? 3
        : synchronizing
        ? 2
        : started
        ? 1
        : 0,
  );

  if (startFailed && reached == 0) {
    events.add(JoinFailed(JoinFailureCode.startFailed, detail: _said(launch)));
  } else if (startFailed && reached < 3) {
    events.add(
      JoinFailed(
        JoinFailureCode.disconnected,
        detail: _said(launch, stopped: true),
      ),
    );
  } else if (multiplayer?.failure?.joinFailureCode case final code?
      when failed && reached < 3) {
    if (code == JoinFailureCode.snapshotFailed) advanceTo(2);
    events.add(
      JoinFailed(code, detail: withoutAddresses(multiplayer!.detail ?? '')),
    );
  } else if (launch?.status == 'cancelled' && reached < 3) {
    advanceTo(1);
    events.add(const JoinCancelRequested());
  }

  final end = terminal?.multiplayer?.endReason;
  if (end != null && reached == 3) {
    events.add(JoinSessionEnded(end.joinEndReason));
  }
  if (activity.processExited) events.add(const JoinGameExited());
  return events;
}

/// How far into the game's part of the join [stage] is; -1 before it.
int _levelOf(JoinStage stage) => switch (stage) {
  JoinStage.launching => 0,
  JoinStage.connecting => 1,
  JoinStage.synchronizing => 2,
  JoinStage.inSession => 3,
  _ => -1,
};

/// What the game said about a launch it could not run, for the player,
/// without addresses, and what to do.
String _said(LaunchOutcome? launch, {bool stopped = false}) {
  final words =
      [
            for (final block in launch?.blocks ?? const <LaunchBlock>[])
              block.message,
            ?launch?.error?.message,
            ?launch?.multiplayer?.detail,
          ]
          .map((text) => withoutAddresses(text.trim()))
          .where((text) => text.isNotEmpty);
  final said = words.isEmpty ? '' : ' It said: ${words.join(' ')}';
  return stopped
      ? 'Robotopia stopped the join.$said Try again.'
      : 'Robotopia could not start the join.$said Try again; if it keeps '
            'failing, open Diagnostics.';
}
