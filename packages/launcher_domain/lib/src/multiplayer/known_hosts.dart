part of '../multiplayer.dart';

/// What the known-hosts record says about a typed address and the server it
/// just served.
enum KnownHostCheck {
  /// The address has not been seen before.
  unknown,

  /// The address served the same server before.
  match,

  /// The address served a different server before: its identity changed,
  /// or something is impersonating it.
  mismatch,
}

/// Trust on first use for addresses typed by hand, which name no server
/// (`docs/internal/MultiplayerLiveSessions.md`, section 8).
///
/// Each entry maps the SHA-256 of a normalized `host:port` to the server id
/// its descriptor proved, so the record never holds an address. It keeps the
/// [maximumEntries] most recent targets, oldest first. [encode] writes the
/// game's own `topiaforge.known-hosts/1` file, and [KnownHosts.decode] reads
/// it as the game does, in any layout, keeping every entry that reads. Links,
/// listings and beacons name a server id and need no entry.
final class KnownHosts {
  KnownHosts._(List<_KnownHost> entries)
    : _entries = List.unmodifiable(entries);

  KnownHosts.empty() : _entries = const [];

  /// Reads a record, keeping what is valid, in any layout another program may
  /// have left it in: white space, a byte-order mark and members in any order
  /// read as the canonical bytes do, so every entry in them is kept. Anything
  /// unreadable gives an empty record, and a later entry for a target
  /// replaces an earlier one. The record is still written only in canonical
  /// form, so a file that is not is salvaged.
  factory KnownHosts.decode(List<int>? bytes) {
    if (bytes == null) return KnownHosts.empty();
    final Object? document;
    try {
      document = CanonicalJson._decodeAnyLayout(
        bytes,
        maximumBytes: maximumBytes,
      );
    } on FormatException {
      return KnownHosts.empty();
    }
    if (document is! Map<String, Object?> ||
        !_hasExactly(document, const {'schema', 'hosts'}) ||
        document['schema'] != schema ||
        document['hosts'] is! List<Object?>) {
      return KnownHosts.empty();
    }
    final entries = <_KnownHost>[];
    for (final item in document['hosts']! as List<Object?>) {
      if (entries.length == maximumEntries) break;
      if (item is! Map<String, Object?> ||
          !_hasExactly(item, const {'target', 'serverId', 'seenAt'})) {
        continue;
      }
      final target = item['target'];
      final serverId = item['serverId'];
      final seenAt = item['seenAt'];
      if (target is! String ||
          !WireText.isHex(target, 32) ||
          serverId is! String ||
          !WireText.isHex(serverId, 32) ||
          seenAt is! int) {
        continue;
      }
      entries
        ..removeWhere((entry) => entry.target == target)
        ..add(_KnownHost(target, serverId, seenAt < 0 ? 0 : seenAt));
    }
    return KnownHosts._(entries);
  }

  static const schema = 'topiaforge.known-hosts/1';
  static const maximumEntries = 256;
  static const maximumBytes = 64 * 1024;

  final List<_KnownHost> _entries;

  int get length => _entries.length;

  /// The record key of [host] and [port]: the lowercase hexadecimal SHA-256
  /// of the UTF-8 text `<normalized host>:<port>`.
  static String targetKey(String host, int port) {
    final normalized = NetworkHost.tryNormalize(host);
    if (normalized == null) {
      throw ArgumentError('A host is a DNS name or an IP address.', 'host');
    }
    if (port < 1 || port > 65535) {
      throw RangeError.range(port, 1, 65535, 'port');
    }
    return PureSha256.hex(utf8.encode('$normalized:$port'));
  }

  KnownHostCheck check(JoinAddressTarget target, String serverId) {
    final key = targetKey(target.host, target.port);
    for (final entry in _entries) {
      if (entry.target == key) {
        return entry.serverId == serverId
            ? KnownHostCheck.match
            : KnownHostCheck.mismatch;
      }
    }
    return KnownHostCheck.unknown;
  }

  /// Records that [target] served [serverId], replacing any earlier record
  /// and forgetting the oldest beyond [maximumEntries].
  KnownHosts remember(
    JoinAddressTarget target,
    String serverId,
    DateTime seenAt,
  ) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError('Not a server id.', 'serverId');
    }
    final key = targetKey(target.host, target.port);
    final seconds = seenAt.millisecondsSinceEpoch ~/ 1000;
    final entries = [
      ..._entries.where((entry) => entry.target != key),
      _KnownHost(key, serverId, seconds < 0 ? 0 : seconds),
    ];
    return KnownHosts._(
      entries.length > maximumEntries ? entries.sublist(1) : entries,
    );
  }

  KnownHosts forget(JoinAddressTarget target) {
    final key = targetKey(target.host, target.port);
    return KnownHosts._([..._entries.where((entry) => entry.target != key)]);
  }

  Uint8List encode() => CanonicalJson.encode({
    'schema': schema,
    'hosts': [
      for (final entry in _entries)
        {
          'target': entry.target,
          'serverId': entry.serverId,
          'seenAt': entry.seenAt,
        },
    ],
  });
}

final class _KnownHost {
  const _KnownHost(this.target, this.serverId, this.seenAt);

  final String target;
  final String serverId;

  /// Unix seconds.
  final int seenAt;
}
