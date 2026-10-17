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
