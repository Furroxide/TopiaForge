/// What the TopiaForge multiplayer service reports about itself and its
/// servers, and why it could not answer, as the launcher's screens and the
/// CLI read them.
///
/// Nothing here holds a network address, an invite code or text the service
/// wrote: every sentence is the launcher's own.
library;

import 'package:launcher_domain/launcher_domain.dart';

/// Why the multiplayer service gave no usable answer.
enum MultiplayerServiceProblem {
  /// This build names no valid service, so nothing was contacted.
  notConfigured,

  /// This computer is not connected to a network.
  offline,

  /// The service could not be reached, or did not answer in time.
  unreachable,

  /// The service is failing or down for now, or this launcher is giving it a
  /// moment after it failed.
  unavailable,

  /// An operator turned off what this needs, for now.
  turnedOff,

  /// The service is turning away requests from this network for a while, or
  /// this launcher holds its own requests back to stay within the service's
  /// limits.
  rateLimited,

  /// The service does not list the server, or has no server for the invite.
  notFound,

  /// The service refused the request as one it cannot take; this launcher
  /// may need an update.
  refused,

  /// The answer could not be read: too large, cut short, not canonical JSON,
  /// or not the shape this launcher expects.
  invalidAnswer,

  /// The server's signed descriptor did not verify, or did not match its
  /// hash, so nothing in it was used.
  descriptorInvalid,
}

/// What the service can turn off.
enum MultiplayerServiceFeature { hosting, invites, signaling, relay }

/// Which Internet paths a session can use while the service reports
/// [MultiplayerServiceFeatures].
enum MultiplayerServicePaths {
  /// The relay and direct paths.
  relayAndDirect,

  /// Direct paths only, besides LAN play: the relay is unavailable.
  directAndLanOnly,
}

/// Why the relay is unavailable.
enum RelayOffReason {
  /// The service halted relay credentials to keep relay costs in check.
  killSwitch,

  /// An operator turned the relay off, or it is not set up.
  turnedOff,
}

/// `GET /v1/status`: what the service offers right now. Clients read it
/// before hosting or joining and degrade from it.
final class MultiplayerServiceFeatures {
  const MultiplayerServiceFeatures({
    required this.hosting,
    required this.invites,
    required this.signaling,
    required this.relay,
    required this.killSwitch,
    required this.heartbeat,
    required this.listingTtl,
    required this.relayCredentialTtl,
  });

  final bool hosting;
  final bool invites;

  /// Hole-punch signaling, which every Internet join and host needs.
  final bool signaling;

  /// The relay as the service reports it; the service already turns it off
  /// while the kill switch is engaged.
  final bool relay;
  final bool killSwitch;

  /// How often a host beats, and how long a listing lives without a beat.
  final Duration heartbeat;
  final Duration listingTtl;

  /// How long one relay credential lasts.
  final Duration relayCredentialTtl;

  /// Whether relay credentials can be had now.
  bool get relayAvailable => relay && !killSwitch;

  /// Without the relay, Internet sessions are direct only; LAN play is as
  /// ever. A player who keeps Hide my IP on then has LAN play alone.
  MultiplayerServicePaths get paths => relayAvailable
      ? MultiplayerServicePaths.relayAndDirect
      : MultiplayerServicePaths.directAndLanOnly;

  /// Why the relay is unavailable; null while it is available.
  RelayOffReason? get relayOffReason => relayAvailable
      ? null
      : killSwitch
      ? RelayOffReason.killSwitch
      : RelayOffReason.turnedOff;

  bool isOn(MultiplayerServiceFeature feature) => switch (feature) {
    MultiplayerServiceFeature.hosting => hosting,
    MultiplayerServiceFeature.invites => invites,
    MultiplayerServiceFeature.signaling => signaling,
    MultiplayerServiceFeature.relay => relayAvailable,
  };

  /// Every feature that is off now, the relay included while the kill switch
  /// holds it.
  Set<MultiplayerServiceFeature> get featuresOff => {
    for (final feature in MultiplayerServiceFeature.values)
      if (!isOn(feature)) feature,
  };

  /// One sentence for each thing a player cannot do now, the relay's first,
  /// with its reason. Empty while everything is on.
  List<String> get notices => [
    if (relayOffReason == RelayOffReason.killSwitch)
      'The relay is paused for now, so only direct and LAN sessions are '
          'available.',
    if (relayOffReason == RelayOffReason.turnedOff)
      'The relay is turned off for now, so only direct and LAN sessions are '
          'available.',
    if (!signaling)
      'Joining and hosting over the Internet are turned off for now.',
    if (!hosting) 'Hosting Internet sessions is turned off for now.',
    if (!invites) 'Invites are turned off for now.',
  ];
}

/// The outcome of reading the service's status.
final class MultiplayerServiceCheck {
  const MultiplayerServiceCheck.available(
    MultiplayerServiceFeatures this.features,
  ) : problem = null,
      message = null,
      retryAfter = null;

  const MultiplayerServiceCheck.unavailable(
    MultiplayerServiceProblem this.problem,
    String this.message, {
    this.retryAfter,
  }) : features = null;

  final MultiplayerServiceFeatures? features;
  final MultiplayerServiceProblem? problem;

  /// A sentence for the player when the status could not be read.
  final String? message;

  /// How long to wait before asking again, when that is known.
  final Duration? retryAfter;

  bool get isAvailable => features != null;
}

/// Which servers the Internet browser lists: those of one game build and
/// protocol major, as the service files them, a page at a time.
final class ServerBrowseFilter {
  ServerBrowseFilter({
    required this.gameBuild,
    required this.protocolMajor,
    this.pageSize = defaultPageSize,
  }) {
    if (!ServerDescriptorV1.isGameBuild(gameBuild)) {
      throw ArgumentError(
        'A game build is 1 to 16 letters, digits, dots, dashes or '
            'underscores.',
        'gameBuild',
      );
    }
    if (protocolMajor < 0 || protocolMajor > maximumProtocolMajor) {
      throw RangeError.range(
        protocolMajor,
        0,
        maximumProtocolMajor,
        'protocolMajor',
      );
    }
    if (pageSize < 1 || pageSize > maximumPageSize) {
      throw RangeError.range(pageSize, 1, maximumPageSize, 'pageSize');
    }
  }

  /// The filter for [gameBuild] and the major version of [protocolVersion],
  /// the multiplayer protocol the installed runtime speaks.
  factory ServerBrowseFilter.forProtocol({
    required String gameBuild,
    required String protocolVersion,
    int pageSize = defaultPageSize,
  }) {
    final major = ServerDescriptorV1.isSemanticVersion(protocolVersion)
        ? int.tryParse(
            protocolVersion.substring(0, protocolVersion.indexOf('.')),
          )
        : null;
    if (major == null) {
      throw ArgumentError(
        'The protocol version is a semantic version.',
        'protocolVersion',
      );
    }
    return ServerBrowseFilter(
      gameBuild: gameBuild,
      protocolMajor: major,
      pageSize: pageSize,
    );
  }

  static const defaultPageSize = 50;
  static const maximumPageSize = 100;

  /// The largest protocol major the service reads: nine digits.
  static const maximumProtocolMajor = 999999999;

  final String gameBuild;
  final int protocolMajor;
  final int pageSize;
}

/// A server found through the service, whose signed descriptor verified
/// against its server id and hashed to the digest the service named before
/// anything here was made.
///
/// [listing] is drawn from that descriptor, with the player count from the
/// host's latest heartbeat, so a browser row built from it holds only what
/// the host signed.
final class InternetServer {
  const InternetServer({
    required this.descriptor,
    required this.listing,
    required this.hostRelayOnly,
  });

  final VerifiedServerDescriptor descriptor;
  final ServerListing listing;

  /// Whether the host keeps its address behind the relay, so joining it
  /// needs the relay whatever the player chose.
  final bool hostRelayOnly;
}

/// The outcome of asking the service for one server.
final class InternetServerLookup {
  const InternetServerLookup.found(InternetServer this.server)
    : problem = null,
      message = null,
      retryAfter = null;

  const InternetServerLookup.failed(
    MultiplayerServiceProblem this.problem,
    String this.message, {
    this.retryAfter,
  }) : server = null;

  final InternetServer? server;
  final MultiplayerServiceProblem? problem;

  /// A sentence for the player when there is no server.
  final String? message;

  /// How long to wait before asking again, when that is known.
  final Duration? retryAfter;

  bool get isFound => server != null;
}
