import 'package:flutter/material.dart';

import 'launcher_theme.dart';
import 'widgets.dart';

part 'session_join_widgets.dart';

/// The approved player-facing wording on address exposure and microphone use,
/// approved as written on 2026-10-14 (`docs/internal/MultiplayerPrivacyText.md`).
/// Every launcher screen uses these strings word for word; a change to their
/// meaning needs a fresh approval before it ships.
abstract final class MultiplayerPrivacyText {
  static const hideMyIp = 'Hide my IP';

  /// The first-use disclosure, before the first host or join.
  static const disclosureTitle = 'Your address and multiplayer';
  static const disclosureExposure =
      'When you play directly with others, they can see your IP address and '
      'you can see theirs. An IP address shows roughly where you are and '
      'which internet provider you use, and someone could use it to disrupt '
      'your connection.';
  static const disclosureHidden =
      'Hide my IP is on by default: your sessions go through a relay or stay '
      'on your local network, so nobody else sees your address. A relayed '
      'session can be a little slower.';
  static const disclosureChoice =
      'You can allow a direct connection for one session when you choose to. '
      'TopiaForge never turns it on for you.';
  static const disclosureButton = 'I understand';

  /// When a session needs a direct connection, for that session only.
  static const directTitle = 'Allow a direct connection?';
  static const directBody =
      'This session can only be reached directly. If you continue, the other '
      'players in it will see your IP address for this session only. Hide my '
      'IP stays on for every other session.';
  static const directAllowButton = 'Allow for this session';
  static const directCancelButton = 'Cancel';

  /// Before the microphone is first used for voice chat.
  static const voiceTitle = 'Voice chat';
  static const voiceBody =
      "Voice chat sends what your microphone picks up to the other players in "
      "the session, through the host's game. TopiaForge does not record it. "
      'Voice stays off until you turn it on, you can choose push-to-talk or '
      'voice activation, and you can turn it off at any time in Voice '
      'settings.';
  static const voiceOnButton = 'Turn on voice chat';
  static const voiceNotNowButton = 'Not now';

  /// The consent sheet's privacy and voice lines.
  static const sheetPrivacyHidden =
      "Privacy: Hide my IP is on, so the host won't see your address.";
  static const sheetPrivacyDirect =
      'Direct connection: the host will see your IP address.';
  static const sheetVoiceOff = 'Voice chat: off';
  static const sheetVoiceOn =
      'Voice chat: on, so players in this session hear your microphone.';
}

/// Shows the first-use disclosure in the approved wording. True when the
/// player pressed its button; false when they closed it another way.
Future<bool> showPrivacyDisclosure(BuildContext context) async {
  final understood = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      key: const Key('privacy-disclosure-dialog'),
      title: const Text(MultiplayerPrivacyText.disclosureTitle),
      content: const SingleChildScrollView(child: PrivacyDisclosureBody()),
      actions: [
        FilledButton(
          autofocus: true,
          onPressed: () => Navigator.of(context).pop(true),
          child: const Text(MultiplayerPrivacyText.disclosureButton),
        ),
      ],
    ),
  );
  return understood ?? false;
}

/// Asks for microphone consent in the approved wording. True only when the
/// player turned voice chat on; "Not now" has the focus, so pressing Enter
/// never consents.
Future<bool> showVoiceConsent(BuildContext context) async {
  final consented = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      key: const Key('voice-consent-dialog'),
      title: const Text(MultiplayerPrivacyText.voiceTitle),
      content: const SingleChildScrollView(
        child: Text(MultiplayerPrivacyText.voiceBody),
      ),
      actions: [
        TextButton(
          autofocus: true,
          onPressed: () => Navigator.of(context).pop(false),
          child: const Text(MultiplayerPrivacyText.voiceNotNowButton),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(true),
          child: const Text(MultiplayerPrivacyText.voiceOnButton),
        ),
      ],
    ),
  );
  return consented ?? false;
}

/// What the launcher tells the player before it first listens for LAN
/// sessions or checks ports: the firewall prompt, the ports, the off switch,
/// and what a port check can and cannot say. The server browser shows it
/// too, so the player reads it once wherever they start.
final class NetworkCheckNotice {
  const NetworkCheckNotice({required this.gamePort, required this.lanPort});

  /// The game's port, UDP and TCP: the one the player hosts on. 0 takes any
  /// free port when hosting, so a port check leaves it alone.
  final int gamePort;

  /// The LAN discovery port, UDP.
  final int lanPort;

  static const title = 'Before TopiaForge uses your network';

  List<String> get lines => [
    'Windows may ask whether to let TopiaForge through its firewall. Allow '
        'private networks only.',
    'Finding LAN sessions listens on UDP $lanPort and asks hosts on your '
        'local network for their session info.',
    'You can turn LAN discovery off in Settings, under Multiplayer.',
    if (gamePort == 0)
      'A port check briefly opens UDP $lanPort on this computer, then closes '
          'it.'
    else
      'A port check briefly opens UDP and TCP $gamePort and UDP $lanPort on '
          'this computer, then closes them.',
    '"Available" means only that the port could be opened just then, not '
        'that anyone can reach it.',
  ];

  /// Shows the notice. When [asking], it stands before an action, offers
  /// Continue and Cancel, and says whether to go on; otherwise it is read
  /// again, offers Close, and gives false.
  Future<bool> show(BuildContext context, {required bool asking}) async {
    final go = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        key: const Key('network-check-notice'),
        title: const Text(title),
        content: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              for (final line in lines)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Padding(
                        padding: EdgeInsets.only(top: 3, right: 8),
                        child: Icon(Icons.info_outline, size: 16),
                      ),
                      Expanded(child: Text(line)),
                    ],
                  ),
                ),
            ],
          ),
        ),
        actions: asking
            ? [
                TextButton(
                  autofocus: true,
                  onPressed: () => Navigator.of(context).pop(false),
                  child: const Text('Cancel'),
                ),
                FilledButton(
                  onPressed: () => Navigator.of(context).pop(true),
                  child: const Text('Continue'),
                ),
              ]
            : [
                FilledButton(
                  autofocus: true,
                  onPressed: () => Navigator.of(context).pop(false),
                  child: const Text('Close'),
                ),
              ],
      ),
    );
    return go ?? false;
  }
}

/// A one-line notice inside a pane: what is going on, in plain words, with
/// an optional action, such as Try again. Its [tone] picks the colour and
/// the icon; the text never depends on colour alone.
class SessionNotice extends StatelessWidget {
  const SessionNotice({
    super.key,
    required this.message,
    this.tone = StatusTone.info,
    this.icon,
    this.action,
  });

  final String message;
  final StatusTone tone;
  final IconData? icon;

  /// A button, such as Try again, shown after the message.
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final color = switch (tone) {
      StatusTone.good => TopiaForgePalette.good,
      StatusTone.info => TopiaForgePalette.accentDark,
      StatusTone.warning => TopiaForgePalette.launchDark,
      StatusTone.danger => TopiaForgePalette.danger,
      StatusTone.neutral => TopiaForgePalette.mutedText,
    };
    final shown =
        icon ??
        switch (tone) {
          StatusTone.good => Icons.check_circle_outline,
          StatusTone.info => Icons.info_outline,
          StatusTone.warning => Icons.warning_amber,
          StatusTone.danger => Icons.error_outline,
          StatusTone.neutral => Icons.info_outline,
        };
    final text = Text(
      message,
      style: Theme.of(
        context,
      ).textTheme.bodyMedium?.copyWith(color: TopiaForgePalette.text),
    );
    return Semantics(
      container: true,
      liveRegion: true,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: color.withValues(alpha: 0.7), width: 2),
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(top: 1, right: 10),
                child: Icon(shown, size: 18, color: color),
              ),
              Expanded(
                child: action == null
                    ? text
                    : Wrap(
                        spacing: 12,
                        runSpacing: 8,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [text, action!],
                      ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
