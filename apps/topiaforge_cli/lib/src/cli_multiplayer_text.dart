/// Approved wording, pinned against MultiplayerPrivacyText.md by tests.
/// Kept Flutter-free so the command line does not depend on launcher_ui.
abstract final class CliMultiplayerText {
  static const disclosure = [
    'Your address and multiplayer',
    'When you play directly with others, they can see your IP address and '
        'you can see theirs. An IP address shows roughly where you are and '
        'which internet provider you use, and someone could use it to disrupt '
        'your connection.',
    'Hide my IP is on by default: your sessions go through a relay or stay '
        'on your local network, so nobody else sees your address. A relayed '
        'session can be a little slower.',
    'You can allow a direct connection for one session when you choose to. '
        'TopiaForge never turns it on for you.',
  ];
  static const direct =
      'This session can only be reached directly. If you continue, the other '
      'players in it will see your IP address for this session only. Hide my '
      'IP stays on for every other session.';
  static const voiceOff = 'Voice chat: off';
  static const voiceOn =
      'Voice chat: on, so players in this session hear your microphone.';
}
