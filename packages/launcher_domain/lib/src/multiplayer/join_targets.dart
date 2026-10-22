part of '../multiplayer.dart';

/// What the player asked to join. No target's [toString] shows an address or
/// an invite code.
sealed class JoinTarget {
  const JoinTarget();

  /// The server the target names before anything is fetched: a server or
  /// direct link's server id, or a listing's. Null for an invite, which the
  /// control plane resolves, and for a typed address, whose server is
  /// learned from the first descriptor it serves.
  String? get pinnedServerId;

  /// Whether the control plane resolves the target before its descriptor is
  /// fetched: invites, server links and Internet listings.
  bool get resolvesThroughService;

  /// The host a direct link or typed address contacts, or null. It is an
  /// address: keep it out of logs, diagnostics and anything mods can read.
  String? get directHost;

  /// The port that goes with [directHost].
  int? get directPort;
}

/// A `topiaforge://join/…` link. A direct link holds an address, and an invite
/// link its code.
final class JoinLinkTarget extends JoinTarget {
  const JoinLinkTarget(this.link);

  final SessionLink link;

  bool get _direct => link.kind == SessionLinkKind.direct;

  @override
  String? get pinnedServerId => link.serverId;

  @override
  bool get resolvesThroughService => !_direct;

  @override
  String? get directHost => _direct ? link.host : null;

  @override
  int? get directPort => _direct ? link.port : null;

  @override
  String toString() => 'link target (${link.kind.name})';
}

/// A row the player picked in the server browser. Only the data layer that
/// found a LAN server knows where it is.
final class JoinListingTarget extends JoinTarget {
  JoinListingTarget({required this.serverId, required this.source}) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError('Not a server id.', 'serverId');
    }
  }

  final String serverId;
  final ServerListingSource source;

  @override
  String get pinnedServerId => serverId;

  @override
  bool get resolvesThroughService => source == ServerListingSource.internet;

  @override
  String? get directHost => null;

  @override
  int? get directPort => null;

  @override
  String toString() => 'listing target (${source.name})';
}

/// A host and port typed by hand, as `topiaforge join <host:port>` takes them.
/// It names no server: the first descriptor the address serves is remembered
/// in [KnownHosts], and a different server there later is an identity change
/// ([JoinFailureCode.identityChanged]) unless the player trusts it.
final class JoinAddressTarget extends JoinTarget {
  JoinAddressTarget({required String host, required int port})
    : host = _normalizedHost(host),
      port = _validPort(port);

  /// The normalized host. It is an address.
  final String host;
  final int port;

  /// Reads typed input: `host`, `host:port`, `[ipv6]` or `[ipv6]:port`, or a
  /// bare IPv6 address. A missing port is [defaultPort]. Returns null when
  /// the input is not an address.
  static JoinAddressTarget? tryParse(
    String? input, {
    int defaultPort = LanBeacon.defaultGamePort,
  }) {
    final text = input?.trim() ?? '';
    if (text.isEmpty || text.length > NetworkHost.maximumLength + 8) {
      return null;
    }
    String host;
    String? port;
    if (text.startsWith('[')) {
      final close = text.indexOf(']');
      if (close < 0) return null;
      host = text.substring(1, close);
      final rest = text.substring(close + 1);
      if (!host.contains(':')) return null;
      if (rest.isNotEmpty) {
        if (!rest.startsWith(':')) return null;
        port = rest.substring(1);
      }
    } else {
      final colons = ':'.allMatches(text).length;
      if (colons == 1) {
        final split = text.indexOf(':');
        host = text.substring(0, split);
        port = text.substring(split + 1);
      } else {
        host = text;
      }
    }
    final number = port == null ? defaultPort : _tryParsePort(port);
    if (number == null || NetworkHost.tryNormalize(host) == null) return null;
    return JoinAddressTarget(host: host, port: number);
  }

  @override
  String? get pinnedServerId => null;

  @override
  bool get resolvesThroughService => false;

  @override
  String get directHost => host;

  @override
  int get directPort => port;

  @override
  String toString() => 'address target';

  static String _normalizedHost(String host) {
    final normalized = NetworkHost.tryNormalize(host);
    if (normalized == null) {
      throw ArgumentError('A host is a DNS name or an IP address.', 'host');
    }
    return normalized;
  }

  static int _validPort(int port) {
    if (port < 1 || port > 65535) {
      throw RangeError.range(port, 1, 65535, 'port');
    }
    return port;
  }
}
