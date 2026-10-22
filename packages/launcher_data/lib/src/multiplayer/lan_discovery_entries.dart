part of 'lan_discovery_client.dart';

/// Where one server's beacon came from: the private handle behind a listing.
final class _Entry {
  _Entry(this.serverId, InternetAddress address, int port)
    : _address = address,
      _port = port,
      local = DescriptorClient.isOnLocalNetwork(address);

  final String serverId;
  InternetAddress _address;
  int _port;

  /// Whether the beacon's sender is on the local network.
  bool local;
  SessionInfo? info;
  bool incompatible = false;
  int roundTripMicros = 0;
  int lastSeen = 0;
  int? queriedAt;

  InternetAddress get address => _address;
  int get port => _port;

  /// The sender's address alone, for per-host rate limits.
  String get hostKey => WireText.toHex(_address.rawAddress);

  bool isAt(InternetAddress address, int port) =>
      port == _port && _sameAddress(address, _address);

  void moveTo(InternetAddress address, int port) {
    _address = address;
    _port = port;
    local = DescriptorClient.isOnLocalNetwork(address);
    info = null;
    incompatible = false;
    roundTripMicros = 0;
    queriedAt = null;
  }
}

final class _Query {
  const _Query(this.address, this.port, this.sentAt);

  final InternetAddress address;
  final int port;
  final int sentAt;

  bool isAt(InternetAddress other, int otherPort) =>
      otherPort == port && _sameAddress(other, address);
}

/// How many of [entries] were last heard from [address], on any port.
int _sessionsAt(Iterable<_Entry> entries, InternetAddress address) =>
    entries.where((entry) => _sameAddress(entry.address, address)).length;

/// The IPv4 address an IPv4-mapped IPv6 address stands for, so both
/// spellings of one host compare and classify alike.
InternetAddress _unmapped(InternetAddress address) {
  final raw = address.rawAddress;
  if (raw.length != 16 || raw[10] != 0xFF || raw[11] != 0xFF) {
    return address;
  }
  for (var index = 0; index < 10; index++) {
    if (raw[index] != 0) return address;
  }
  return InternetAddress.fromRawAddress(Uint8List.sublistView(raw, 12));
}

bool _sameAddress(InternetAddress left, InternetAddress right) {
  final a = left.rawAddress;
  final b = right.rawAddress;
  if (a.length != b.length) return false;
  for (var index = 0; index < a.length; index++) {
    if (a[index] != b[index]) return false;
  }
  return true;
}

final class _Counters {
  var datagramsHeard = 0;
  var invalidBeacons = 0;
  var queriesSent = 0;
  var queriesThrottled = 0;
  var queriesUnsent = 0;
  var repliesAccepted = 0;
  var repliesRefused = 0;
  var unusableInfo = 0;
  var serversDropped = 0;
  var faults = 0;
  var querySocketsReplaced = 0;

  LanDiscoveryCounters toCounters() => LanDiscoveryCounters(
    datagramsHeard: datagramsHeard,
    invalidBeacons: invalidBeacons,
    queriesSent: queriesSent,
    queriesThrottled: queriesThrottled,
    queriesUnsent: queriesUnsent,
    repliesAccepted: repliesAccepted,
    repliesRefused: repliesRefused,
    unusableInfo: unusableInfo,
    serversDropped: serversDropped,
    faults: faults,
    querySocketsReplaced: querySocketsReplaced,
  );
}

/// The rate limits on info queries: [LanDiscoveryClient.maximumQueriesPerSecond]
/// overall, and [LanDiscoveryClient.maximumQueriesPerHost] to one host per
/// requery window. A forged beacon names its own sender, so these bound how
/// much a flood of them can make the browser send anywhere.
final class _QueryBudget {
  final List<int> _recent = [];
  final Map<String, List<int>> _byHost = {};

  bool admit(String hostKey, int now) {
    _recent.removeWhere((time) => now - time >= 1000000);
    if (_recent.length >= LanDiscoveryClient.maximumQueriesPerSecond) {
      return false;
    }
    if (!_byHost.containsKey(hostKey) &&
        _byHost.length >= LanDiscoveryClient.maximumServers * 2) {
      _byHost.clear();
    }
    final window = LanDiscoveryClient.requery.inMicroseconds;
    final times = _byHost.putIfAbsent(hostKey, () => [])
      ..removeWhere((time) => now - time >= window);
    if (times.length >= LanDiscoveryClient.maximumQueriesPerHost) return false;
    times.add(now);
    _recent.add(now);
    return true;
  }

  /// Forgets hosts not asked within the last window.
  void forgetIdle(int now) => _byHost.removeWhere(
    (_, times) =>
        times.isEmpty ||
        now - times.last >= LanDiscoveryClient.requery.inMicroseconds,
  );

  void clear() {
    _recent.clear();
    _byHost.clear();
  }
}

/// The listed sessions, as a snapshot: what each entry's info says, by
/// server id, with the senders beyond the local network only counted.
extension _Listing on LanDiscoveryClient {
  LanBrowseSnapshot _listing() {
    final listings = <ServerListing>[];
    final incompatible = <String>[];
    var pending = 0;
    var beyond = 0;
    final ids = _entries.keys.toList()..sort();
    for (final id in ids) {
      final entry = _entries[id]!;
      final info = entry.info;
      if (!entry.local) {
        beyond++;
      } else if (entry.incompatible) {
        incompatible.add(id);
      } else if (info == null) {
        pending++;
      } else {
        listings.add(
          ServerListing.fromSessionInfo(
            info,
            pingMilliseconds: (entry.roundTripMicros ~/ 1000).clamp(
              0,
              ServerListing.maximumPingMilliseconds,
            ),
          ),
        );
      }
    }
    return LanBrowseSnapshot(
      status: LanDiscoveryStatus.listening,
      listings: listings,
      pending: pending,
      incompatibleServerIds: incompatible,
      beyondLocalNetwork: beyond,
    );
  }
}
