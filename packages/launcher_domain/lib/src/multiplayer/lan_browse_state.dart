part of '../multiplayer.dart';

/// What LAN discovery has found, as the launcher shows it. Nothing here holds
/// an address: hosts are named by server id, and where each was heard stays
/// inside the data layer's LAN client.

/// Whether the launcher is listening for LAN sessions.
enum LanDiscoveryStatus {
  /// Not browsing.
  stopped,

  /// The player turned LAN discovery off in settings.
  disabled,

  /// Listening for beacons on the discovery port.
  listening,

  /// Discovery cannot run just now; see [LanDiscoveryProblem].
  unavailable,
}

/// Why LAN discovery could not listen.
enum LanDiscoveryProblem {
  /// Another program holds the discovery port without sharing it.
  portInUse,

  /// The system refused the port, as a firewall or policy can.
  permissionDenied,

  /// Any other failure to open a socket.
  failed,

  /// The player's multiplayer settings could not be read, so whether they
  /// allow discovery is unknown, and it stays off. No socket was opened.
  settingsUnreadable,
}

/// Running totals for diagnostics. They count datagrams, never who sent them.
final class LanDiscoveryCounters {
  const LanDiscoveryCounters({
    this.datagramsHeard = 0,
    this.invalidBeacons = 0,
    this.queriesSent = 0,
    this.queriesThrottled = 0,
    this.queriesUnsent = 0,
    this.repliesAccepted = 0,
    this.repliesRefused = 0,
    this.unusableInfo = 0,
    this.serversDropped = 0,
    this.faults = 0,
    this.querySocketsReplaced = 0,
  });

  /// Datagrams that arrived on the discovery port.
  final int datagramsHeard;

  /// Of those, the ones that were not a beacon.
  final int invalidBeacons;

  /// Info queries sent to hosts on the local network.
  final int queriesSent;

  /// Queries held back by the rate limits.
  final int queriesThrottled;

  /// Queries the socket could not send.
  final int queriesUnsent;

  /// Replies that matched a query.
  final int repliesAccepted;

  /// Replies refused: larger than the query, malformed, unmatched, late or
  /// from somewhere the query did not go.
  final int repliesRefused;

  /// Matched replies whose info did not read or named another server.
  final int unusableInfo;

  /// Beacons ignored because the list, or their sender's share of it, was
  /// full.
  final int serversDropped;

  /// Datagrams whose handling failed unexpectedly; each was dropped.
  final int faults;

  /// Query sockets the system closed, each after a send it refused, and
  /// replaced with a new one.
  final int querySocketsReplaced;

  Map<String, Object?> toJson() => {
    'datagramsHeard': datagramsHeard,
    'invalidBeacons': invalidBeacons,
    'queriesSent': queriesSent,
    'queriesThrottled': queriesThrottled,
    'queriesUnsent': queriesUnsent,
    'repliesAccepted': repliesAccepted,
    'repliesRefused': repliesRefused,
    'unusableInfo': unusableInfo,
    'serversDropped': serversDropped,
    'faults': faults,
    'querySocketsReplaced': querySocketsReplaced,
  };
}

/// One moment of LAN browsing.
final class LanBrowseSnapshot {
  LanBrowseSnapshot({
    required this.status,
    this.problem,
    List<ServerListing> listings = const [],
    this.pending = 0,
    List<String> incompatibleServerIds = const [],
    this.beyondLocalNetwork = 0,
    this.counters = const LanDiscoveryCounters(),
  }) : listings = List.unmodifiable(listings),
       incompatibleServerIds = List.unmodifiable(incompatibleServerIds);

  /// Nothing is being browsed.
  static final stopped = LanBrowseSnapshot(status: LanDiscoveryStatus.stopped);

  /// The player turned LAN discovery off.
  static final disabled = LanBrowseSnapshot(
    status: LanDiscoveryStatus.disabled,
  );

  final LanDiscoveryStatus status;

  /// Why browsing is [LanDiscoveryStatus.unavailable].
  final LanDiscoveryProblem? problem;

  /// Sessions on the local network whose host answered with info naming the
  /// server its beacon did, in server id order. Nothing in a listing is
  /// trusted until its descriptor verifies.
  final List<ServerListing> listings;

  /// Sessions heard on the local network whose info has not arrived yet.
  final int pending;

  /// Sessions whose host speaks another transport version, so they cannot be
  /// joined from here.
  final List<String> incompatibleServerIds;

  /// Sessions announced from beyond the local network. Browsing never
  /// queries them, so they never learn this player's address.
  final int beyondLocalNetwork;

  final LanDiscoveryCounters counters;

  /// A sentence for the player when browsing is not listening, or null.
  String? get message => switch (status) {
    LanDiscoveryStatus.listening => null,
    LanDiscoveryStatus.stopped => null,
    LanDiscoveryStatus.disabled =>
      'LAN discovery is off. Turn it on in Multiplayer settings to find '
          'sessions on your local network.',
    LanDiscoveryStatus.unavailable => switch (problem) {
      LanDiscoveryProblem.portInUse =>
        'Another program is using the LAN discovery port '
            '${LanBeacon.discoveryPort}, so local sessions cannot be found.',
      LanDiscoveryProblem.permissionDenied =>
        'The system blocked the LAN discovery port '
            '${LanBeacon.discoveryPort}, so local sessions cannot be found.',
      LanDiscoveryProblem.settingsUnreadable =>
        'Your multiplayer settings could not be read just now, so LAN '
            'discovery stays off. Try again in a moment.',
      _ => 'LAN discovery could not start, so local sessions cannot be found.',
    },
  };

  LanBrowseSnapshot withCounters(LanDiscoveryCounters counters) =>
      LanBrowseSnapshot(
        status: status,
        problem: problem,
        listings: listings,
        pending: pending,
        incompatibleServerIds: incompatibleServerIds,
        beyondLocalNetwork: beyondLocalNetwork,
        counters: counters,
      );

  /// The diagnostic form: status, counts and counters, never a listing's
  /// contents or anything about where a host is.
  Map<String, Object?> toJson() => {
    'status': status.name,
    'problem': problem?.name,
    'listed': listings.length,
    'pending': pending,
    'incompatible': incompatibleServerIds.length,
    'beyondLocalNetwork': beyondLocalNetwork,
    'counters': counters.toJson(),
  };
}
