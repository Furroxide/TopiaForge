part of '../launch_resolution.dart';

/// The server a join-session launch goes to. Every target pins the server id,
/// the SHA-256 of the exact descriptor the join was planned from, and that
/// descriptor's package digest: the game joins only that server, and a
/// descriptor that hashes differently fails as `admission-changed`, which
/// the launcher plans once more.
///
/// Only a [LaunchDirectTarget] holds an address, and only in the session's
/// own launch request: no progress, outcome, channel, projection or log
/// carries it. No target's [toString] shows it.
sealed class LaunchSessionTarget {
  LaunchSessionTarget._({
    required String serverId,
    required String descriptorSha256,
    required String packageDigest,
  }) : serverId = _sha256Hex(serverId),
       descriptorSha256 = _sha256Hex(descriptorSha256),
       packageDigest = _sha256Hex(packageDigest);

  /// The target for joining [joining] with [plan]: the server the plan was
  /// made for, its exact descriptor and its package digest.
  factory LaunchSessionTarget.forPlan(JoinTarget joining, JoinPlan plan) {
    final server = plan.server;
    final pinned = joining.pinnedServerId;
    if (pinned != null && pinned != server.serverId) {
      throw ArgumentError('The plan is for another server.', 'plan');
    }
    final pins = (
      serverId: server.serverId,
      descriptorSha256: server.descriptorSha256,
      packageDigest: server.packageDigest,
    );
    return switch (joining) {
      JoinLinkTarget(link: final link) => switch (link.kind) {
        SessionLinkKind.invite => LaunchInviteTarget(
          inviteCode: link.inviteCode!,
          serverId: pins.serverId,
          descriptorSha256: pins.descriptorSha256,
          packageDigest: pins.packageDigest,
        ),
        SessionLinkKind.server => LaunchListingTarget(
          source: ServerListingSource.internet,
          serverId: pins.serverId,
          descriptorSha256: pins.descriptorSha256,
          packageDigest: pins.packageDigest,
        ),
        SessionLinkKind.direct => LaunchDirectTarget(
          host: link.host!,
          port: link.port!,
          serverId: pins.serverId,
          descriptorSha256: pins.descriptorSha256,
          packageDigest: pins.packageDigest,
        ),
      },
      JoinListingTarget(source: final source) => LaunchListingTarget(
        source: source,
        serverId: pins.serverId,
        descriptorSha256: pins.descriptorSha256,
        packageDigest: pins.packageDigest,
      ),
      JoinAddressTarget(host: final host, port: final port) =>
        LaunchDirectTarget(
          host: host,
          port: port,
          serverId: pins.serverId,
          descriptorSha256: pins.descriptorSha256,
          packageDigest: pins.packageDigest,
        ),
    };
  }

  factory LaunchSessionTarget.fromJson(Object? value) {
    if (value is! Map<String, Object?>) {
      throw const FormatException('Invalid session target.');
    }
    const pins = {'descriptorSha256', 'kind', 'packageDigest', 'serverId'};
    switch (value['kind']) {
      case 'invite':
        final json = _object(
          value,
          {...pins, 'inviteCode'},
          {...pins, 'inviteCode'},
        );
        return LaunchInviteTarget(
          inviteCode: _inviteCode(json['inviteCode']),
          serverId: _sha256Hex(json['serverId']),
          descriptorSha256: _sha256Hex(json['descriptorSha256']),
          packageDigest: _sha256Hex(json['packageDigest']),
        );
      case 'listing':
        final json = _object(value, {...pins, 'source'}, {...pins, 'source'});
        return LaunchListingTarget(
          source: _named(
            json['source'],
            ServerListingSource.values,
            (item) => item.name,
          ),
          serverId: _sha256Hex(json['serverId']),
          descriptorSha256: _sha256Hex(json['descriptorSha256']),
          packageDigest: _sha256Hex(json['packageDigest']),
        );
      case 'direct':
        final json = _object(
          value,
          {...pins, 'host', 'port'},
          {...pins, 'host', 'port'},
        );
        return LaunchDirectTarget(
          host: _directHost(json['host']),
          port: _ranged(json['port'], 1, 65535),
          serverId: _sha256Hex(json['serverId']),
          descriptorSha256: _sha256Hex(json['descriptorSha256']),
          packageDigest: _sha256Hex(json['packageDigest']),
        );
      default:
        throw const FormatException('Unknown session target kind.');
    }
  }

  final String serverId;
  final String descriptorSha256;
  final String packageDigest;

  /// `invite`, `listing` or `direct`.
  String get kind;

  /// Whether the control plane must be reached to find the server.
  bool get needsService;

  Map<String, Object?> toJson() => {
    'descriptorSha256': descriptorSha256,
    'kind': kind,
    'packageDigest': packageDigest,
    'serverId': serverId,
  };
}

/// An invite code the control plane resolved to [serverId].
final class LaunchInviteTarget extends LaunchSessionTarget {
  LaunchInviteTarget({
    required String inviteCode,
    required super.serverId,
    required super.descriptorSha256,
    required super.packageDigest,
  }) : inviteCode = _inviteCode(inviteCode),
       super._();

  final String inviteCode;

  @override
  String get kind => 'invite';

  @override
  bool get needsService => true;

  @override
  Map<String, Object?> toJson() => {
    ...super.toJson(),
    'inviteCode': inviteCode,
  };

  @override
  String toString() => 'invite target';
}

/// A server picked from the Internet browser or found on the LAN.
final class LaunchListingTarget extends LaunchSessionTarget {
  LaunchListingTarget({
    required this.source,
    required super.serverId,
    required super.descriptorSha256,
    required super.packageDigest,
  }) : super._();

  final ServerListingSource source;

  @override
  String get kind => 'listing';

  @override
  bool get needsService => source == ServerListingSource.internet;

  @override
  Map<String, Object?> toJson() => {...super.toJson(), 'source': source.name};

  @override
  String toString() => 'listing target (${source.name})';
}

/// A host and port, from a direct link or typed by hand. [host] is an
/// address: it exists only in the session's own launch request.
final class LaunchDirectTarget extends LaunchSessionTarget {
  LaunchDirectTarget({
    required String host,
    required int port,
    required super.serverId,
    required super.descriptorSha256,
    required super.packageDigest,
  }) : host = _directHost(host),
       port = _ranged(port, 1, 65535),
       super._();

  final String host;
  final int port;

  /// Whether reaching [host] stays on the local network, which relay-only
  /// privacy allows without the player's consent to direct play.
  bool get onLocalNetwork => _isLocalNetworkHost(host);

  @override
  String get kind => 'direct';

  @override
  bool get needsService => false;

  @override
  Map<String, Object?> toJson() => {
    ...super.toJson(),
    'host': host,
    'port': port,
  };

  @override
  String toString() => 'direct target';
}
