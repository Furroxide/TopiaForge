part of 'launcher_event.dart';

// Multiplayer events. `launcher_session_link_actions.dart` dispatches them,
// and the multiplayer section adds its own here.

/// An event for the launcher's multiplayer state.
sealed class MultiplayerEvent extends LauncherEvent {
  const MultiplayerEvent();
}

/// A `topiaforge://` link reached the launcher: from its command line at
/// start, or later, handed over by another start or opened by the system.
/// Every link arrives as this one event. The domain reads it, and a valid
/// link waits for the player's consent: it never joins by itself.
final class SessionLinkReceived extends MultiplayerEvent {
  const SessionLinkReceived(this.arrival);

  final SessionLinkArrival arrival;
}

/// The pending session link, or its refusal, was dismissed.
final class SessionLinkDismissed extends MultiplayerEvent {
  const SessionLinkDismissed();
}

/// Applies the link-handling setting once the launcher has started: while it
/// is on, the launcher registers itself for `topiaforge://` links.
final class SessionLinkHandlingStarted extends MultiplayerEvent {
  const SessionLinkHandlingStarted();
}

/// The player turned link handling on or off. Turning it off removes this
/// launcher's registration; the result arrives as [SessionLinkHandlingUpdated].
final class SessionLinkHandlingChanged extends MultiplayerEvent {
  const SessionLinkHandlingChanged(this.enabled);

  final bool enabled;
}

/// The registration followed the setting: at start, or after a change.
final class SessionLinkHandlingUpdated extends MultiplayerEvent {
  const SessionLinkHandlingUpdated(this.status, {this.announce = true});

  final SessionLinkHandlingStatus status;

  /// Whether the status bar says what happened. The start's registration
  /// stays quiet.
  final bool announce;
}
