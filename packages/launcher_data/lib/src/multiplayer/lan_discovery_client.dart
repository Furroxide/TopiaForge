/// Finds sessions on the local network the way the game's own browser does
/// (`docs/internal/MultiplayerLiveSessions.md`, section 16).
library;

import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';
import 'descriptor_client.dart';
import 'lan_datagrams.dart';
import 'lan_discovery_state.dart';
import 'lan_sockets.dart';

part 'lan_discovery_entries.dart';

/// Monotonic time in microseconds.
typedef LanClock = int Function();

/// Starts a periodic timer; tests drive time through it.
typedef LanTicker = Timer Function(Duration interval, void Function() tick);

/// Listens for [LanBeacon]s on UDP 27251, asks each host on the local network
/// for its session info with a 1,200-byte [LanInfoQuery], and lists the
/// sessions whose info names the server their beacon did.
///
/// - **Privacy.** Browsing is always relay-only: a beacon from beyond the
///   local network is counted but never answered, so its sender never learns
///   this player's address. A [ServerListing] never carries an address. Where
///   each server was heard is a private handle inside this client, and only
///   [fetchDescriptor] uses it.
/// - **Bounds.** A reply is accepted only when it is no larger than the
///   query, carries the id of a query still outstanding, and comes from the
///   socket that query went to. At most [maximumServers] sessions and
///   [maximumOutstandingQueries] queries are kept, and queries are rate
///   limited overall and per host, so a flood of forged beacons cannot turn
///   the browser into an amplifier.
/// - **Robustness.** Datagrams are hostile input: nothing on the network path
///   throws, and anything malformed is dropped and counted.
/// - **Sockets.** The listener binds [LanBeacon.discoveryPort] with address
///   reuse, so the game can listen too. Broadcasts reach only a socket bound
///   to every address, so that is the default; tests pass loopback.
final class LanDiscoveryClient {
  LanDiscoveryClient({
    InternetAddress? listenAddress,
    this.port = LanBeacon.discoveryPort,
    InternetAddress? queryAddress,
    LanSocketBinder? bind,
    DescriptorClient? descriptors,
    Random? random,
    LanClock? clock,
    LanTicker? ticker,
  }) : _listenAddress = listenAddress ?? InternetAddress.anyIPv4,
       _queryAddress = queryAddress ?? InternetAddress.anyIPv4,
       _bind = bind ?? bindLanDatagramSocket,
       _descriptors = descriptors ?? DescriptorClient(),
       _random = random ?? Random.secure(),
       _clock = clock ?? _monotonicClock(),
       _ticker = ticker ?? _periodic;

  /// A session silent this long leaves the list.
  static const expiry = Duration(seconds: 6);

  /// A host is asked again at most this often while its beacons arrive.
  static const requery = Duration(seconds: 2);

  /// A query not answered within this long is forgotten.
  static const queryLifetime = Duration(seconds: 5);
  static const sweepInterval = Duration(seconds: 1);
  static const maximumServers = 64;
  static const maximumOutstandingQueries = 64;
  static const maximumQueriesPerSecond = 32;

  /// Queries to one host per [requery] window, enough for a machine running
  /// several peers.
  static const maximumQueriesPerHost = 8;

  /// The discovery port; tests use another.
  final int port;

  final InternetAddress _listenAddress;
  final InternetAddress _queryAddress;
  final LanSocketBinder _bind;
  final DescriptorClient _descriptors;
  final Random _random;
  final LanClock _clock;
  final LanTicker _ticker;
  final _changes = StreamController<LanBrowseSnapshot>.broadcast();
  final Map<String, _Entry> _entries = {};
  final Map<String, _Query> _queries = {};
  final _budget = _QueryBudget();
  final _counters = _Counters();
  LanDatagramSocket? _listener;
  LanDatagramSocket? _querier;
  Timer? _sweeper;
  Future<LanBrowseSnapshot>? _starting;
  var _generation = 0;
  var _disposed = false;
  LanBrowseSnapshot _snapshot = LanBrowseSnapshot.stopped;

  /// The latest state, with current counters.
  LanBrowseSnapshot get snapshot =>
      _snapshot.withCounters(_counters.toCounters());

  /// Every change to the listed sessions or the status.
  Stream<LanBrowseSnapshot> get snapshots => _changes.stream;

  bool get isListening => _listener != null;

  /// The port the listener is bound to while listening: [port], or the one
  /// the system chose when [port] is zero, as in tests.
  int? get listeningPort => _listener?.port;

  /// Starts listening. Never throws: a port that cannot be opened gives an
  /// [LanDiscoveryStatus.unavailable] snapshot.
  Future<LanBrowseSnapshot> start() {
    if (_disposed) return Future.value(snapshot);
    if (_listener != null) return Future.value(snapshot);
    return _starting ??= _start(_generation);
  }

  Future<LanBrowseSnapshot> _start(int generation) async {
    LanDatagramSocket? listener;
    LanDatagramSocket? querier;
    try {
      listener = await _bind(_listenAddress, port, reuseAddress: true);
      querier = await _bind(_queryAddress, 0, reuseAddress: false);
    } on Object catch (error) {
      listener?.close();
      if (generation == _generation) _starting = null;
      if (generation != _generation || _disposed) return snapshot;
      return _publish(
        LanBrowseSnapshot(
          status: LanDiscoveryStatus.unavailable,
          problem: _problemOf(error),
        ),
      );
    }
    if (generation != _generation || _disposed) {
      listener.close();
      querier.close();
      return snapshot;
    }
    _starting = null;
    _listener = listener;
    _querier = querier;
    listener.listen(_onBeacon);
    querier.listen(_onReply);
    _sweeper = _ticker(sweepInterval, _sweep);
    return _publish(_listing());
  }

  /// Stops listening and forgets every session. Harmless when stopped.
  /// [disabled] reports that the player turned discovery off.
  Future<LanBrowseSnapshot> stop({bool disabled = false}) async {
    _generation++;
    _starting = null;
    _sweeper?.cancel();
    _sweeper = null;
    _listener?.close();
    _querier?.close();
    _listener = null;
    _querier = null;
    _entries.clear();
    _queries.clear();
    _budget.clear();
    if (_disposed) return snapshot;
    return _publish(
      disabled ? LanBrowseSnapshot.disabled : LanBrowseSnapshot.stopped,
    );
  }

  Future<void> dispose() async {
    if (_disposed) return;
    await stop();
    _disposed = true;
    await _changes.close();
  }

  /// Fetches the descriptor of the LAN listing [request] names, from where
  /// its beacon came. Under relay-only a host heard from beyond the local
  /// network is refused before anything is contacted.
  Future<DescriptorFetch> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) async {
    final target = request.target;
    if (target is! JoinListingTarget ||
        target.source != ServerListingSource.lan) {
      throw ArgumentError(
        'Only a LAN listing is fetched through LAN discovery.',
        'request',
      );
    }
    final entry = _entries[target.serverId];
    if (entry == null) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.unreachable,
        'The session is no longer on the local network.',
      );
    }
    if (!request.allowsDirect && !entry.local) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.directNotAllowed,
        'This session was announced from beyond your local network, and '
        'reaching it directly would show it your address.',
      );
    }
    return _descriptors.fetchFrom(
      entry.address,
      entry.port,
      allowDirect: request.allowsDirect,
      expectedServerId: request.expectedServerId ?? target.serverId,
      cancellation: cancellation,
    );
  }

  void _onBeacon(Datagram datagram) {
    try {
      _counters.datagramsHeard++;
      final beacon = LanBeacon.tryDecode(datagram.data);
      if (beacon == null) {
        _counters.invalidBeacons++;
        return;
      }
      if (_heard(beacon, _unmapped(datagram.address), _clock())) {
        _publish(_listing());
      }
    } on Object {
      _counters.faults++;
    }
  }

  /// Records a beacon; true when the list changed.
  bool _heard(LanBeacon beacon, InternetAddress sender, int now) {
    var entry = _entries[beacon.serverId];
    final fresh = entry == null;
    if (entry == null) {
      if (_entries.length >= maximumServers) {
        _counters.serversDropped++;
        return false;
      }
      entry = _Entry(beacon.serverId, sender, beacon.gamePort);
      _entries[beacon.serverId] = entry;
    }
    // What the old socket answered says nothing about a new one: a moved
    // server waits for a reply from there that names it again.
    final moved = !fresh && !entry.isAt(sender, beacon.gamePort);
    if (moved) entry.moveTo(sender, beacon.gamePort);
    entry.lastSeen = now;
    final queriedAt = entry.queriedAt;
    if (entry.local &&
        (queriedAt == null || now - queriedAt >= requery.inMicroseconds)) {
      _query(entry, now);
    }
    return fresh || moved;
  }

  void _query(_Entry entry, int now) {
    final querier = _querier;
    if (querier == null) return;
    if (!_budget.admit(entry.hostKey, now)) {
      _counters.queriesThrottled++;
      return;
    }
    _expireQueries(now);
    if (_queries.length >= maximumOutstandingQueries) _forgetOldestQuery();
    final id = Uint8List(LanInfoQuery.idBytes);
    String? key;
    for (var attempt = 0; attempt < 8 && key == null; attempt++) {
      for (var index = 0; index < id.length; index++) {
        id[index] = _random.nextInt(256);
      }
      final candidate = WireText.toHex(id);
      if (!_queries.containsKey(candidate)) key = candidate;
    }
    // A random source that keeps repeating itself gets no query at all.
    if (key == null) {
      _counters.queriesUnsent++;
      return;
    }
    _queries[key] = _Query(entry.address, entry.port, now);
    entry.queriedAt = now;
    if (querier.send(LanInfoQuery.encode(id), entry.address, entry.port)) {
      _counters.queriesSent++;
    } else {
      _counters.queriesUnsent++;
    }
  }

  void _onReply(Datagram datagram) {
    try {
      final now = _clock();
      _expireQueries(now);
      // A reply is never larger than the query it answers.
      final reply = LanInfoReply.tryDecode(datagram.data);
      final key = reply == null ? null : WireText.toHex(reply.queryId);
      final query = key == null ? null : _queries[key];
      if (reply == null ||
          query == null ||
          !query.isAt(_unmapped(datagram.address), datagram.port)) {
        _counters.repliesRefused++;
        return;
      }
      _queries.remove(key);
      _counters.repliesAccepted++;
      if (_answered(reply, query, now)) _publish(_listing());
    } on Object {
      _counters.faults++;
    }
  }

  /// Applies a matched reply; true when the list changed. Its info counts
  /// only for the beacon that named the same server at that socket, so a
  /// beacon naming someone else there never borrows the reply.
  bool _answered(LanInfoReply reply, _Query query, int now) {
    if (!reply.isCompatible) {
      var changed = false;
      for (final entry in _entries.values) {
        if (entry.isAt(query.address, query.port) && !entry.incompatible) {
          entry.incompatible = true;
          changed = true;
        }
      }
      return changed;
    }
    final info = reply.sessionInfo;
    final entry = info == null ? null : _entries[info.serverId];
    if (entry == null || !entry.isAt(query.address, query.port)) {
      _counters.unusableInfo++;
      return false;
    }
    entry
      ..info = info
      ..incompatible = false
      ..roundTripMicros = now - query.sentAt;
    return true;
  }

  void _sweep() {
    try {
      final now = _clock();
      _expireQueries(now);
      final before = _entries.length;
      _entries.removeWhere(
        (_, entry) => now - entry.lastSeen >= expiry.inMicroseconds,
      );
      _budget.forgetIdle(now);
      if (_entries.length != before) _publish(_listing());
    } on Object {
      _counters.faults++;
    }
  }

  void _expireQueries(int now) => _queries.removeWhere(
    (_, query) => now - query.sentAt >= queryLifetime.inMicroseconds,
  );

  void _forgetOldestQuery() {
    String? oldest;
    var oldestAt = 0;
    _queries.forEach((key, query) {
      if (oldest == null || query.sentAt < oldestAt) {
        oldest = key;
        oldestAt = query.sentAt;
      }
    });
    _queries.remove(oldest);
  }

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

  LanBrowseSnapshot _publish(LanBrowseSnapshot next) {
    _snapshot = next;
    final current = snapshot;
    if (!_changes.isClosed) _changes.add(current);
    return current;
  }

  /// The IPv4 address an IPv4-mapped IPv6 address stands for, so both
  /// spellings of one host compare and classify alike.
  static InternetAddress _unmapped(InternetAddress address) {
    final raw = address.rawAddress;
    if (raw.length != 16 || raw[10] != 0xFF || raw[11] != 0xFF) {
      return address;
    }
    for (var index = 0; index < 10; index++) {
      if (raw[index] != 0) return address;
    }
    return InternetAddress.fromRawAddress(Uint8List.sublistView(raw, 12));
  }

  /// Address-in-use and access-denied codes on Windows, Linux and macOS.
  static LanDiscoveryProblem _problemOf(Object error) {
    final code = error is SocketException ? error.osError?.errorCode : null;
    return switch (code) {
      10048 || 98 || 48 => LanDiscoveryProblem.portInUse,
      10013 || 13 => LanDiscoveryProblem.permissionDenied,
      _ => LanDiscoveryProblem.failed,
    };
  }

  static LanClock _monotonicClock() {
    final stopwatch = Stopwatch()..start();
    return () => stopwatch.elapsedMicroseconds;
  }

  static Timer _periodic(Duration interval, void Function() tick) =>
      Timer.periodic(interval, (_) => tick());
}
