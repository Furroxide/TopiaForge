import 'multiplayer.dart';

/// What a host is told before a public Internet session starts. Its approved
/// wording is the section "When a player hosts a public Internet session" of
/// `docs/internal/MultiplayerPrivacyText.md`; the CLI and the launcher show it
/// word for word, from here.
abstract final class HostActivityText {
  static const publicSessions =
      'Public sessions count toward rough activity numbers on the TopiaForge '
      'status page. The numbers move in steps of 5 sessions and 10 players, '
      'appear only once those minimums are reached, and are shown at least 15 '
      'minutes late. Invite-only and local network sessions are never '
      'counted. The numbers are kept for 90 days.';

  /// The notice for a session hosted with [visibility]: the approved text for
  /// a public session, and nothing for invite-only or local network ones,
  /// which are never counted.
  static String? noticeFor(ServerVisibility visibility) =>
      visibility == ServerVisibility.public ? publicSessions : null;
}
