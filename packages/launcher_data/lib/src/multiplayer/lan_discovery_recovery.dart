part of 'lan_discovery_client.dart';

/// A query socket the system closed is replaced. The system closes a socket
/// whose send it refused, such as a query to a network it cannot reach, and
/// nothing sent through it arrives after that, so without a new one every
/// later host would wait unlisted until browsing restarted.
extension _QuerySocketRecovery on LanDiscoveryClient {
  /// Binds a new query socket in the closed one's place, once at a time.
  /// Queries already sent through the closed one go unanswered and expire;
  /// each host is asked again on its next beacon.
  void _replaceQuerier() {
    if (_replacingQuerier || _disposed) return;
    _replacingQuerier = true;
    unawaited(_bindQuerier(_generation));
  }

  Future<void> _bindQuerier(int generation) async {
    LanDatagramSocket? next;
    try {
      next = await _bind(_queryAddress, 0, reuseAddress: false);
    } on Object {
      next = null;
    }
    final current = _querier;
    if (generation != _generation || _disposed || current == null) {
      next?.close();
      return;
    }
    _replacingQuerier = false;
    if (next == null) return;
    current.close();
    _querier = next;
    next.listen(_onReply);
    _counters.querySocketsReplaced++;
  }
}
