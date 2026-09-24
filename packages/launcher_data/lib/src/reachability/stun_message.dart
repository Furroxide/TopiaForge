/// Minimal RFC 5389 / RFC 5780 STUN binding codec for the launcher's reachability probe.
///
/// Scope is deliberately narrow: encode a Binding Request (optionally carrying CHANGE-REQUEST), and decode a Binding
/// Success Response far enough to read XOR-MAPPED-ADDRESS and OTHER-ADDRESS. **This is not an ICE agent, a TURN
/// client, or a WebRTC stack**, and must not grow into one — see the non-goals in
/// `docs/internal/LauncherReachabilityProbe.md`. It sends a handful of small datagrams to learn one boolean-shaped
/// fact about the local network and then stops.
library;

import 'dart:typed_data';

/// A network endpoint observed during a probe.
///
/// Confined to the data layer on purpose. Addresses are compared here and only the comparison results cross into
/// `launcher_domain`, which has no type capable of holding one. Never persist or log an instance of this.
class StunEndpoint {
  StunEndpoint(this.address, this.port)
    : assert(address.length == 4 || address.length == 16),
      assert(port >= 0 && port <= 65535);

  /// Raw address bytes: 4 for IPv4, 16 for IPv6.
  final Uint8List address;
  final int port;

  bool get isIPv4 => address.length == 4;

  @override
  bool operator ==(Object other) {
    if (other is! StunEndpoint || other.port != port) return false;
    if (other.address.length != address.length) return false;
    for (var index = 0; index < address.length; index++) {
      if (other.address[index] != address[index]) return false;
    }
    return true;
  }

  @override
  int get hashCode => Object.hash(port, Object.hashAll(address));

  /// Debug form only. Callers must not put this in logs, reports, or diagnostic bundles.
  @override
  String toString() =>
      'StunEndpoint(${address.length == 4 ? 'v4' : 'v6'}:$port)';
}

/// A decoded Binding Success Response.
class StunBindingResponse {
  const StunBindingResponse({required this.mapped, this.otherAddress});

  /// The reflexive transport address the server observed.
  final StunEndpoint mapped;

  /// The server's alternate address and port, when it advertises one (RFC 5780 OTHER-ADDRESS).
  final StunEndpoint? otherAddress;
}

/// Encoding and decoding for the two STUN messages the probe needs.
class StunCodec {
  const StunCodec();

  static const _bindingRequest = 0x0001;
  static const _bindingSuccess = 0x0101;
  static const _magicCookie = 0x2112A442;

  static const _attrMappedAddress = 0x0001;
  static const _attrChangeRequest = 0x0003;
  static const _attrXorMappedAddress = 0x0020;
  static const _attrOtherAddress = 0x802C;

  static const _changeAddressFlag = 0x04;
  static const _changePortFlag = 0x02;

  /// Length of the fixed STUN header.
  static const headerLength = 20;

  /// Length of a transaction id.
  static const transactionIdLength = 12;

  /// Builds a Binding Request.
  ///
  /// [changeAddress] and [changePort] add a CHANGE-REQUEST attribute, asking the server to answer from a different
  /// address and/or port. That is how filtering behaviour is observed: a response that arrives from somewhere the
  /// client never sent to proves the NAT let it in.
  Uint8List encodeBindingRequest({
    required Uint8List transactionId,
    bool changeAddress = false,
    bool changePort = false,
  }) {
    if (transactionId.length != transactionIdLength) {
      throw ArgumentError.value(
        transactionId.length,
        'transactionId',
        'A STUN transaction id is exactly $transactionIdLength bytes',
      );
    }
    final wantsChange = changeAddress || changePort;
    final bodyLength = wantsChange ? 8 : 0;
    final message = Uint8List(headerLength + bodyLength);
    final view = ByteData.view(message.buffer);

    view.setUint16(0, _bindingRequest);
    view.setUint16(2, bodyLength);
    view.setUint32(4, _magicCookie);
    message.setRange(8, 8 + transactionIdLength, transactionId);

    if (wantsChange) {
      view.setUint16(headerLength, _attrChangeRequest);
      view.setUint16(headerLength + 2, 4);
      var flags = 0;
      if (changeAddress) flags |= _changeAddressFlag;
      if (changePort) flags |= _changePortFlag;
      view.setUint32(headerLength + 4, flags);
    }
    return message;
  }

  /// Decodes a Binding Success Response, or returns `null` when [datagram] is not one, is truncated, does not match
  /// [transactionId], or carries no usable mapped address.
  ///
  /// Every failure is a `null`, never a throw: this parses bytes from the open internet, and a probe that crashes on
  /// a malformed reply is worse than a probe that reports nothing.
  StunBindingResponse? decodeBindingResponse(
    Uint8List datagram,
    Uint8List transactionId,
  ) {
    if (datagram.length < headerLength) return null;
    final view = ByteData.view(
      datagram.buffer,
      datagram.offsetInBytes,
      datagram.length,
    );
    if (view.getUint16(0) != _bindingSuccess) return null;
    if (view.getUint32(4) != _magicCookie) return null;
    for (var index = 0; index < transactionIdLength; index++) {
      if (datagram[8 + index] != transactionId[index]) return null;
    }
    final bodyLength = view.getUint16(2);
    if (headerLength + bodyLength > datagram.length) return null;

    StunEndpoint? mapped;
    StunEndpoint? plainMapped;
    StunEndpoint? other;

    var cursor = headerLength;
    final end = headerLength + bodyLength;
    while (cursor + 4 <= end) {
      final type = view.getUint16(cursor);
      final length = view.getUint16(cursor + 2);
      final valueStart = cursor + 4;
      if (valueStart + length > end) return null;

      switch (type) {
        case _attrXorMappedAddress:
          mapped ??= _decodeAddress(
            view,
            valueStart,
            length,
            datagram,
            xor: true,
          );
        case _attrMappedAddress:
          plainMapped ??= _decodeAddress(
            view,
            valueStart,
            length,
            datagram,
            xor: false,
          );
        case _attrOtherAddress:
          other ??= _decodeAddress(
            view,
            valueStart,
            length,
            datagram,
            xor: false,
          );
      }
      // Attributes are padded to a 4-byte boundary.
      cursor = valueStart + ((length + 3) & ~3);
    }

    final resolved = mapped ?? plainMapped;
    if (resolved == null) return null;
    return StunBindingResponse(mapped: resolved, otherAddress: other);
  }

  StunEndpoint? _decodeAddress(
    ByteData view,
    int start,
    int length,
    Uint8List datagram, {
    required bool xor,
  }) {
    if (length < 8) return null;
    final family = view.getUint8(start + 1);
    final size = switch (family) {
      0x01 => 4,
      0x02 => 16,
      _ => 0,
    };
    if (size == 0 || length < 4 + size) return null;

    var port = view.getUint16(start + 2);
    final address = Uint8List(size);
    for (var index = 0; index < size; index++) {
      address[index] = view.getUint8(start + 4 + index);
    }

    if (xor) {
      port ^= (_magicCookie >> 16) & 0xFFFF;
      // IPv4 XORs against the cookie; IPv6 XORs against the cookie followed by the transaction id.
      for (var index = 0; index < size; index++) {
        address[index] ^= index < 4
            ? (_magicCookie >> (24 - (index * 8))) & 0xFF
            : datagram[8 + index - 4];
      }
    }
    return StunEndpoint(address, port);
  }
}
