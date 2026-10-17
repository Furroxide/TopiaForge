/// The transport's info query and reply, as the launcher's LAN browser sends
/// and reads them (`docs/internal/MultiplayerSecureTransport.md`, section 12).
library;

import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

/// A browser's question to a host, asked before any handshake: `E4`, the
/// transport version, a random 8-byte query id, and zero padding to exactly
/// 1,200 bytes. The query fills a whole datagram and the host's reply never
/// exceeds it, so the exchange cannot amplify traffic toward a forged source.
abstract final class LanInfoQuery {
  static const packetType = 0xE4;

  /// The transport version this launcher speaks.
  static const version = 1;

  /// Every query is exactly this long: the transport's largest datagram.
  static const size = 1200;
  static const idBytes = 8;

  /// The query with [queryId], padded to [size].
  static Uint8List encode(List<int> queryId) {
    if (queryId.length != idBytes ||
        queryId.any((value) => value < 0 || value > 0xFF)) {
      throw ArgumentError('A query id is 8 bytes.', 'queryId');
    }
    return Uint8List(size)
      ..[0] = packetType
      ..[1] = version
      ..setRange(2, 2 + idBytes, queryId);
  }
}

/// A host's answer to a [LanInfoQuery]: `E5`, the responder's transport
/// version, the query id, the info length as two bytes little-endian, and
/// the info itself. A host of another version answers with its version and
/// no info, so the browser can say why it cannot join.
final class LanInfoReply {
  LanInfoReply._(this.version, this.queryId, this.info);

  static const packetType = 0xE5;
  static const headerBytes = 1 + 1 + LanInfoQuery.idBytes + 2;

  /// The most info one reply carries: the query size less the header.
  static const maximumInfoBytes = LanInfoQuery.size - headerBytes;

  /// The responder's transport version.
  final int version;
  final Uint8List queryId;

  /// The responder's session info bytes; empty from a host of another
  /// version.
  final Uint8List info;

  bool get isCompatible => version == LanInfoQuery.version;

  /// Reads a reply, or returns null for anything that is not exactly one:
  /// another type, a length field that disagrees with the datagram, or a
  /// datagram larger than the query it would answer. It never throws, since
  /// a datagram is hostile input.
  static LanInfoReply? tryDecode(List<int> datagram) {
    if (datagram.length < headerBytes ||
        datagram.length > LanInfoQuery.size ||
        datagram[0] != packetType ||
        datagram.any((value) => value < 0 || value > 0xFF)) {
      return null;
    }
    final length = datagram[10] | (datagram[11] << 8);
    if (length != datagram.length - headerBytes) return null;
    return LanInfoReply._(
      datagram[1],
      Uint8List.fromList(datagram.sublist(2, 2 + LanInfoQuery.idBytes)),
      Uint8List.fromList(datagram.sublist(headerBytes)),
    );
  }

  /// The session info this reply carries, or null when it carries none that
  /// reads as `topiaforge.session-info/1`. Never throws.
  SessionInfo? get sessionInfo {
    if (!isCompatible || info.isEmpty) return null;
    try {
      return SessionInfo.tryDecode(info);
    } on Object {
      return null;
    }
  }

  /// Builds a reply, for tests and tools that stand in for a host.
  static Uint8List encode({
    required List<int> queryId,
    required List<int> info,
    int version = LanInfoQuery.version,
  }) {
    if (queryId.length != LanInfoQuery.idBytes) {
      throw ArgumentError('A query id is 8 bytes.', 'queryId');
    }
    if (info.length > maximumInfoBytes) {
      throw ArgumentError('Session info must fit one reply.', 'info');
    }
    return Uint8List(headerBytes + info.length)
      ..[0] = packetType
      ..[1] = version
      ..setRange(2, 2 + LanInfoQuery.idBytes, queryId)
      ..[10] = info.length & 0xFF
      ..[11] = info.length >> 8
      ..setRange(headerBytes, headerBytes + info.length, info);
  }
}
