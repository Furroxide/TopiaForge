part of 'launcher_event.dart';

// The join flow. `launcher_multiplayer_join_actions.dart` and
// `launcher_multiplayer_join_steps.dart` dispatch these. The player's events
// name the flow they answer, and the answers of work beside the event lane
// name the flow and the attempt they belong to, so an answer for an older
// one is dropped.

/// An event of the join flow.
sealed class MultiplayerJoinEvent extends MultiplayerEvent {
  const MultiplayerJoinEvent();
}

/// Joins [target]. The server browser's JOIN and the direct-connect dialog
/// add it; a session link and the game's handoff reach the same flow. Nothing
/// is downloaded, installed or started before the player consents.
final class MultiplayerJoinRequested extends MultiplayerJoinEvent {
  const MultiplayerJoinRequested(
    this.target, {
    this.origin = JoinFlowOrigin.browser,
  });

  final JoinTarget target;
  final JoinFlowOrigin origin;

  @override
  String toString() => 'join requested (${origin.name}, $target)';
}

/// The player answered the flow's question: the disclosure's one button, or
/// "Allow for this session" ([yes]) or Cancel on the direct prompt.
final class MultiplayerJoinQuestionAnswered extends MultiplayerJoinEvent {
  const MultiplayerJoinQuestionAnswered({
    required this.flow,
    required this.yes,
  });

  final int flow;
  final bool yes;
}

/// The player ticked or cleared the consent sheet's developer-mode box for
/// the mods only the host has. Join stays off until it is ticked.
final class MultiplayerJoinHostServedTicked extends MultiplayerJoinEvent {
  const MultiplayerJoinHostServedTicked({
    required this.flow,
    required this.consent,
    required this.ticked,
  });

  final int flow;

  /// The exact consent shown when the box was changed.
  final JoinSessionState consent;
  final bool ticked;
}

/// The player pressed Join on the consent sheet. [closeOwnedGame] says
/// whether the sheet told them that joining closes the game this launcher
/// started. Whether the developer-mode box is ticked is the flow's own
/// `hostServedTicked`.
final class MultiplayerJoinConsented extends MultiplayerJoinEvent {
  const MultiplayerJoinConsented({
    required this.flow,
    required this.consent,
    this.closeOwnedGame = false,
  });

  final int flow;

  /// The exact consent shown when Join was pressed.
  final JoinSessionState consent;
  final bool closeOwnedGame;
}

/// The player asked to stop: Cancel, the close button, or Escape.
final class MultiplayerJoinCancelled extends MultiplayerJoinEvent {
  const MultiplayerJoinCancelled(this.flow);

  final int flow;
}

/// The player asked to try the join again from its start.
final class MultiplayerJoinRetried extends MultiplayerJoinEvent {
  const MultiplayerJoinRetried(this.flow);

  final int flow;
}

/// The player chose to trust the server a typed address now answers as.
final class MultiplayerJoinTrusted extends MultiplayerJoinEvent {
  const MultiplayerJoinTrusted(this.flow);

  final int flow;
}

/// The player asked to allow a direct connection after a join stopped
/// because it needed one: the approved prompt asks first.
final class MultiplayerJoinDirectAsked extends MultiplayerJoinEvent {
  const MultiplayerJoinDirectAsked(this.flow);

  final int flow;
}

/// The player closed the flow's outcome.
final class MultiplayerJoinClosed extends MultiplayerJoinEvent {
  const MultiplayerJoinClosed(this.flow);

  final int flow;
}

/// The preferences a new flow starts from were read.
final class MultiplayerJoinPreferencesRead extends MultiplayerJoinEvent {
  const MultiplayerJoinPreferencesRead(this.flow, this.preferences);

  final int flow;
  final MultiplayerPreferences preferences;
}

/// The disclosure's acknowledgement was saved, or [preferences] is null
/// because it could not be.
final class MultiplayerJoinDisclosureSaved extends MultiplayerJoinEvent {
  const MultiplayerJoinDisclosureSaved(this.flow, this.preferences);

  final int flow;
  final MultiplayerPreferences? preferences;
}

/// The launcher answered the join's step of [attempt]; [answer] is null when
/// the step's work was cancelled, or failed in a way it could not word.
final class MultiplayerJoinStepAnswered extends MultiplayerJoinEvent {
  const MultiplayerJoinStepAnswered({
    required this.flow,
    required this.attempt,
    required this.answer,
  });

  final int flow;
  final int attempt;
  final JoinEvent? answer;
}

/// Download or install progress of [attempt].
final class MultiplayerJoinProgressed extends MultiplayerJoinEvent {
  const MultiplayerJoinProgressed({
    required this.flow,
    required this.attempt,
    required this.progress,
  });

  final int flow;
  final int attempt;
  final JoinEvent progress;
}

/// Who runs the game, for the consent sheet.
final class MultiplayerJoinGameChecked extends MultiplayerJoinEvent {
  const MultiplayerJoinGameChecked(this.flow, this.running);

  final int flow;
  final JoinGameRunning running;
}

/// How the launching stage of [attempt] went.
final class MultiplayerJoinLaunched extends MultiplayerJoinEvent {
  const MultiplayerJoinLaunched({
    required this.flow,
    required this.attempt,
    required this.launch,
  });

  final int flow;
  final int attempt;
  final JoinLaunch launch;
}

/// A game this launcher started asked it to run a join.
final class MultiplayerJoinHandoffReceived extends MultiplayerJoinEvent {
  const MultiplayerJoinHandoffReceived(this.arrival);

  final JoinHandoffArrival arrival;
}
