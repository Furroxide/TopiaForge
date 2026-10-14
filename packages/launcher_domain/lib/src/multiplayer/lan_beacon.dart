part of '../multiplayer.dart';

/// The announcement a LAN host broadcasts to UDP port 27251 every two
/// seconds, from its game socket: `E6`, the beacon version, the 32-byte
/// server id, and the game port as two bytes, little-endian.
///
/// A beacon is an unauthenticated hint. A browser that hears one sends an
/// info query to the sender at that port, then fetches and verifies the
/// signed descriptor before trusting anything. The beacon itself carries no
/// address; the sender's is known only to the data layer that received it.
final class LanBeacon {
  LanBeacon({required this.serverId, required this.gamePort}) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError(
        'A server id is 64 lowercase hexadecimal characters.',
        'serverId',
      );
    }
    if (gamePort < 1 || gamePort > 65535) {
      throw RangeError.range(gamePort, 1, 65535, 'gamePort');
    }
  }

  static const packetType = 0xE6;
  static const version = 1;
  static const size = 36;
  static const discoveryPort = 27251;
  static const defaultGamePort = 27250;
  static const interval = Duration(seconds: 2);

  final String serverId;
  final int gamePort;

  Uint8List encode() {
    final datagram = Uint8List(size)
      ..[0] = packetType
      ..[1] = version
      ..setRange(2, 34, WireText.tryParseHex(serverId, 32)!)
      ..[34] = gamePort & 0xFF
      ..[35] = gamePort >> 8;
    return datagram;
  }

  /// Reads a beacon, or returns null for anything of another size, type or
  /// version, with port zero, or holding a value that is not a byte. It
  /// never throws: a datagram is hostile input.
  static LanBeacon? tryDecode(List<int> datagram) {
    if (datagram.length != size ||
        datagram.any((value) => value < 0 || value > 0xFF) ||
        datagram[0] != packetType ||
        datagram[1] != version) {
      return null;
    }
    final port = datagram[34] | (datagram[35] << 8);
    if (port == 0) return null;
    return LanBeacon(
      serverId: WireText.toHex(datagram.sublist(2, 34)),
      gamePort: port,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is LanBeacon &&
      other.serverId == serverId &&
      other.gamePort == gamePort;

  @override
  int get hashCode => Object.hash(serverId, gamePort);
}
