part of 'lan_discovery_client.dart';

/// A join's claim on LAN browsing ([LanDiscoveryClient.hold]). While any
/// hold stands, a plain stop waits, so the entry that reaches a join's host
/// stays alive through its downloads.
final class LanBrowseHold {
  LanBrowseHold._(this._client);

  final LanDiscoveryClient _client;
  var _released = false;

  bool get isReleased => _released;

  /// Lets browsing stop again: a stop that waited for the last hold, or
  /// browsing a hold had to start, stops now. Harmless twice.
  void release() {
    if (_released) return;
    _released = true;
    _client._release();
  }
}

/// A download waiting for a server's beacon, since [since] (monotonic
/// microseconds).
final class _Arrival {
  _Arrival(this.since);

  final int since;
  final _done = Completer<void>();

  Future<void> get heard => _done.future;

  void wake() {
    if (!_done.isCompleted) _done.complete();
  }
}

/// Holds, beacon waits and host-served downloads: what a join's downloading
/// stage needs from LAN discovery.
extension _Holds on LanDiscoveryClient {
  Future<LanBrowseHold> _hold(Future<bool> Function()? mayStart) async {
    final hold = LanBrowseHold._(this);
    _holds++;
    if (_disposed ||
        _listener != null ||
        _starting != null ||
        mayStart == null) {
      return hold;
    }
    final generation = _generation;
    var allowed = false;
    try {
      allowed = await mayStart();
    } on Object {
      allowed = false;
    }
    // Turning discovery off, or finding it unavailable, while the settings
    // were read wins: either stops browsing, which moves the generation on.
    if (allowed &&
        !hold._released &&
        !_disposed &&
        generation == _generation &&
        _listener == null &&
        _starting == null) {
      _stopWhenReleased = true;
      await _startListening();
    }
    return hold;
  }

  void _release() {
    if (_holds > 0) _holds--;
    if (_holds == 0 && _stopWhenReleased) unawaited(stop());
  }

  /// The entry of [serverId], waiting while browsing runs for a beacon that
  /// makes one, until the first sweep after [LanDiscoveryClient.expiry];
  /// null when none came, or when [cancellation] stopped the wait.
  Future<_Entry?> _awaitEntry(
    String serverId,
    CancellationToken? cancellation,
  ) async {
    final present = _entries[serverId];
    if (present != null ||
        _disposed ||
        (_listener == null && _starting == null)) {
      return present;
    }
    final arrival = _Arrival(_clock());
    final waiting = _arrivals.putIfAbsent(serverId, () => [])..add(arrival);
    final registration = cancellation?.register(arrival.wake);
    try {
      await arrival.heard;
    } finally {
      registration?.dispose();
      waiting.remove(arrival);
      if (waiting.isEmpty && identical(_arrivals[serverId], waiting)) {
        _arrivals.remove(serverId);
      }
    }
    if (cancellation?.isCancelled ?? false) return null;
    return _entries[serverId];
  }

  /// Wakes the downloads waiting for [serverId]'s beacon, or every waiting
  /// download, which then look again.
  void _wakeArrivals([String? serverId]) {
    final lists = serverId == null
        ? [..._arrivals.values]
        : [?_arrivals[serverId]];
    for (final waiting in lists) {
      for (final arrival in [...waiting]) {
        arrival.wake();
      }
    }
  }

  /// Gives up on beacons awaited for [LanDiscoveryClient.expiry]: a session
  /// silent that long would have left the list.
  void _expireArrivals(int now) {
    for (final waiting in [..._arrivals.values]) {
      for (final arrival in [...waiting]) {
        if (now - arrival.since >= LanDiscoveryClient.expiry.inMicroseconds) {
          arrival.wake();
        }
      }
    }
  }

  Future<JoinHostPackageResult> _fetchPackage(
    JoinDownloadRequest request,
    String sha256,
    File target,
    void Function(int bytesReceived, int bytesTotal)? onProgress,
    CancellationToken? cancellation,
  ) async {
    final listing = request.target;
    if (listing is! JoinListingTarget ||
        listing.source != ServerListingSource.lan) {
      throw ArgumentError(
        'Only a LAN listing is fetched through LAN discovery.',
        'request',
      );
    }
    final entry = await _awaitEntry(listing.serverId, cancellation);
    if (cancellation?.isCancelled ?? false) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.cancelled);
    }
    if (entry == null) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.notHeard);
    }
    return _packages.fetchFrom(
      entry.address,
      entry.port,
      sha256,
      target,
      allowDirect: request.allowsDirect,
      onProgress: onProgress,
      cancellation: cancellation,
    );
  }
}
