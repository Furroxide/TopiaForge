part of '../multiplayer.dart';

enum SessionLinkKind {
  /// `topiaforge://join/<code>`: an invite, resolved through the control
  /// plane.
  invite,

  /// `topiaforge://join/server/<server id>`: a listed server, found through
  /// the control plane.
  server,

  /// `topiaforge://join/direct/<server id>/<host>/<port>`: a direct address,
  /// pinned by the server id.
  direct,
}

/// A `topiaforge://join/…` session link, in the grammar the game, the
/// launcher and the control plane share: no query, fragment or
/// percent-encoding, at most 512 characters, the scheme in any case and
/// everything else exact.
///
/// A link never joins on its own; it opens the consent flow. A direct link
/// carries the host's address, so [toString] leaves the address out; only
/// [canonical] spells it, for the network layer and the launcher's own link
/// handling.
final class SessionLink {
  SessionLink._(
    this.kind, {
    this.inviteCode,
    this.serverId,
    this.host,
    this.port,
  });

  /// A link to the invite [code], read forgivingly ([InviteCode]).
  factory SessionLink.invite(String code) {
    final normalized = InviteCode.tryNormalize(code);
    if (normalized == null) {
      throw ArgumentError.value(
        code,
        'code',
        'An invite code is 10 Crockford base32 characters.',
      );
    }
    return SessionLink._(SessionLinkKind.invite, inviteCode: normalized);
  }

  /// A link to the listed server [serverId].
  factory SessionLink.server(String serverId) {
    _requireServerId(serverId);
    return SessionLink._(SessionLinkKind.server, serverId: serverId);
  }

  /// A direct link to [host] and [port], pinned by [serverId].
  factory SessionLink.direct({
    required String serverId,
    required String host,
    required int port,
  }) {
    _requireServerId(serverId);
    final normalizedHost = NetworkHost.tryNormalize(host);
    if (normalizedHost == null) {
      throw ArgumentError.value(
        host,
        'host',
        'A host is a DNS name or an IP address.',
      );
    }
    if (port < 1 || port > 65535) {
      throw RangeError.range(port, 1, 65535, 'port');
    }
    return SessionLink._(
      SessionLinkKind.direct,
      serverId: serverId,
      host: normalizedHost,
      port: port,
    );
  }

  static const scheme = 'topiaforge';
  static const maximumLength = 512;
  static const _prefix = 'topiaforge://join/';

  final SessionLinkKind kind;

  /// The canonical invite code of an invite link.
  final String? inviteCode;

  /// The server id of a server or direct link.
  final String? serverId;

  /// The normalized host of a direct link. It is an address: keep it out of
  /// logs, diagnostics and anything mods can read.
  final String? host;

  /// The port of a direct link.
  final int? port;

  /// Reads [text], or returns null when it is not a session link.
  static SessionLink? tryParse(String? text) {
    if (text == null ||
        text.length > maximumLength ||
        text.length <= _prefix.length) {
      return null;
    }
    // Printable ASCII only, without a query, fragment or percent-encoding.
    for (var index = 0; index < text.length; index++) {
      final unit = text.codeUnitAt(index);
      if (unit <= 0x20 ||
          unit > 0x7E ||
          unit == 0x3F ||
          unit == 0x23 ||
          unit == 0x25) {
        return null;
      }
    }
    // Only the scheme ignores case; the rest of the prefix is exact.
    if (text.substring(0, scheme.length).toLowerCase() != scheme ||
        !text.startsWith(_prefix.substring(scheme.length), scheme.length)) {
      return null;
    }
    final segments = text.substring(_prefix.length).split('/');
    try {
      if (segments.length == 1) return SessionLink.invite(segments[0]);
      if (segments.length == 2 && segments[0] == 'server') {
        return SessionLink.server(segments[1]);
      }
      if (segments.length == 4 && segments[0] == 'direct') {
        final port = _tryParsePort(segments[3]);
        if (port == null) return null;
        return SessionLink.direct(
          serverId: segments[1],
          host: segments[2],
          port: port,
        );
      }
    } on ArgumentError {
      return null;
    }
    return null;
  }

  /// The canonical link. For a direct link it holds the host's address.
  String get canonical => switch (kind) {
    SessionLinkKind.invite => '$_prefix$inviteCode',
    SessionLinkKind.server => '${_prefix}server/$serverId',
    SessionLinkKind.direct => '${_prefix}direct/$serverId/$host/$port',
  };

  /// The canonical link with a direct link's host and port left out, safe for
  /// logs and diagnostics.
  @override
  String toString() => kind == SessionLinkKind.direct
      ? '${_prefix}direct/$serverId/<address withheld>'
      : canonical;

  @override
  bool operator ==(Object other) =>
      other is SessionLink && other.canonical == canonical;

  @override
  int get hashCode => canonical.hashCode;

  static void _requireServerId(String serverId) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(
        serverId,
        'serverId',
        'A server id is 64 lowercase hexadecimal characters.',
      );
    }
  }
}
