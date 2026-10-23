import 'package:launcher_domain/launcher_domain.dart';

import 'session_link_inbox.dart';

part 'launcher_multiplayer_settings_state.dart';

/// The launcher's multiplayer state: the session link intake and link
/// handling, Settings → Multiplayer and Diagnostics → Network. The browser
/// and the join flow add their own fields here.
final class MultiplayerViewState {
  const MultiplayerViewState({
    this.sessionLink = const SessionLinkIdle(),
    this.linkArrivals = 0,
    this.linkHandling,
    this.settings = const MultiplayerSettingsView(),
    this.network = const MultiplayerNetworkView(),
  });

  /// The newest session link and what became of it. A newer link replaces
  /// it, so links never queue up.
  final SessionLinkIntake sessionLink;

  /// How many session links have arrived. Each intake carries its number,
  /// so the same link arriving twice still reads as a new arrival.
  final int linkArrivals;

  /// The link-handling setting and its registration, once applied. Null
  /// until the launcher applies it, and where nothing handles links.
  final SessionLinkHandlingStatus? linkHandling;

  /// Settings → Multiplayer: the player's preferences, and whether they can
  /// be changed now.
  final MultiplayerSettingsView settings;

  /// Diagnostics → Network: the service, the ports and LAN discovery.
  final MultiplayerNetworkView network;

  MultiplayerViewState copyWith({
    SessionLinkIntake? sessionLink,
    int? linkArrivals,
    SessionLinkHandlingStatus? linkHandling,
    MultiplayerSettingsView? settings,
    MultiplayerNetworkView? network,
  }) => MultiplayerViewState(
    sessionLink: sessionLink ?? this.sessionLink,
    linkArrivals: linkArrivals ?? this.linkArrivals,
    linkHandling: linkHandling ?? this.linkHandling,
    settings: settings ?? this.settings,
    network: network ?? this.network,
  );
}

/// What became of the newest session link.
sealed class SessionLinkIntake {
  const SessionLinkIntake();
}

/// No session link is pending.
final class SessionLinkIdle extends SessionLinkIntake {
  const SessionLinkIdle();
}

/// Placeholder for the multiplayer consent flow, which replaces it.
///
/// A valid link waits here, and nothing joins: the consent flow will plan
/// the join from trusted manifests and ask the player before anything is
/// installed or started. A direct [link] holds the host's address, and an
/// invite [link] a code that lets anyone who holds it join, so it is never
/// logged; its `toString` withholds both.
final class SessionLinkConsentPlaceholder extends SessionLinkIntake {
  const SessionLinkConsentPlaceholder({
    required this.link,
    required this.source,
    required this.arrival,
  });

  final SessionLink link;
  final SessionLinkSource source;
  final int arrival;
}

/// A link the domain refused. The text is not kept.
final class SessionLinkRefused extends SessionLinkIntake {
  const SessionLinkRefused({
    required this.refusal,
    required this.source,
    required this.arrival,
  });

  final SessionLinkRefusal refusal;
  final SessionLinkSource source;
  final int arrival;
}

/// The player-facing reason for [refusal]. It never repeats the link.
String sessionLinkRefusalMessage(SessionLinkRefusal refusal) =>
    switch (refusal) {
      SessionLinkRefusal.tooLong =>
        'The link is longer than ${SessionLink.maximumLength} characters, so '
            'it is not a TopiaForge session link.',
      SessionLinkRefusal.notSessionLink =>
        'The link is not a TopiaForge session link. Session links start '
            'with topiaforge://join/.',
      SessionLinkRefusal.invalid =>
        'The session link is damaged or incomplete. Ask the host for a new '
            'link.',
    };

/// How people call a link of [kind].
String sessionLinkKindLabel(SessionLinkKind kind) => switch (kind) {
  SessionLinkKind.invite => 'a multiplayer invite link',
  SessionLinkKind.server => 'a multiplayer server link',
  SessionLinkKind.direct => 'a direct multiplayer link',
};
