part of '../multiplayer.dart';

/// The hosts a direct session link may name, in the one spelling every
/// runtime writes: dotted-decimal IPv4, RFC 5952 IPv6 without brackets (it
/// fills a whole path segment), or a lowercase DNS name.
///
/// A host is an address, so values that pass through here belong only to the
/// network layer and the launcher. They never reach mods, snapshots, logs,
/// diagnostics or crash reports.
abstract final class NetworkHost {
  /// The longest host a link carries, as DNS limits names.
  static const maximumLength = 253;

  /// Normalizes [host], or returns null when it is not an IPv4 address, an
  /// IPv6 address or a DNS name. An IPv4-mapped IPv6 address becomes the IPv4
  /// address it maps. Only ASCII letters change case.
  static String? tryNormalize(String? host) {
    if (host == null || host.isEmpty || host.length > maximumLength) {
      return null;
    }
    if (host.contains(':')) {
      for (var index = 0; index < host.length; index++) {
        final unit = host.codeUnitAt(index);
        if (_hexDigit(unit) < 0 && unit != 0x3A && unit != 0x2E) return null;
      }
      final address = _tryParseIPv6(host);
      return address == null ? null : formatIPv6(address);
    }
    if (_isDottedQuad(host)) return host;
    return _tryNormalizeName(host);
  }

  /// Writes a 16-byte IPv6 address as RFC 5952 recommends: lowercase, no
  /// leading zeros, and the longest run of two or more zero groups
  /// compressed, the first winning a tie. An IPv4-mapped address becomes
  /// dotted decimal.
  static String formatIPv6(List<int> address) {
    if (address.length != 16) {
      throw ArgumentError.value(
        address.length,
        'address',
        'An IPv6 address is 16 bytes.',
      );
    }
    var mapped = address[10] == 0xFF && address[11] == 0xFF;
    for (var index = 0; mapped && index < 10; index++) {
      mapped = address[index] == 0;
    }
    if (mapped) return address.sublist(12).join('.');

    final groups = [
      for (var index = 0; index < 8; index++)
        (address[index * 2] << 8) | address[index * 2 + 1],
    ];
    var bestStart = -1;
    var bestLength = 1;
    for (var index = 0; index < 8;) {
      if (groups[index] != 0) {
        index++;
        continue;
      }
      final start = index;
      while (index < 8 && groups[index] == 0) {
        index++;
      }
      if (index - start > bestLength) {
        bestStart = start;
        bestLength = index - start;
      }
    }
    final buffer = StringBuffer();
    for (var index = 0; index < 8; index++) {
      if (index == bestStart) {
        buffer.write('::');
        index += bestLength - 1;
        continue;
      }
      final text = buffer.toString();
      if (text.isNotEmpty && !text.endsWith(':')) buffer.write(':');
      buffer.write(groups[index].toRadixString(16));
    }
    return buffer.toString();
  }

  /// Parses RFC 4291 text (hexadecimal groups, at most one `::`, and an
  /// optional dotted-decimal tail) into 16 bytes, or returns null.
  static Uint8List? _tryParseIPv6(String text) {
    final gap = text.indexOf('::');
    if (gap >= 0 && text.indexOf('::', gap + 1) >= 0) return null;
    final head = gap < 0 ? text : text.substring(0, gap);
    final tail = gap < 0 ? '' : text.substring(gap + 2);
    final headGroups = _groups(head, last: gap < 0);
    final tailGroups = _groups(tail, last: true);
    if (headGroups == null || tailGroups == null) return null;
    final explicit = headGroups.length + tailGroups.length;
    if (gap < 0 ? explicit != 8 : explicit > 7) return null;
    final groups = [
      ...headGroups,
      for (var index = explicit; index < 8; index++) 0,
      ...tailGroups,
    ];
    final address = Uint8List(16);
    for (var index = 0; index < 8; index++) {
      address[index * 2] = groups[index] >> 8;
      address[index * 2 + 1] = groups[index] & 0xFF;
    }
    return address;
  }

  /// Reads the colon-separated groups of one side of `::`. Only the last
  /// group of the whole address may be a dotted-decimal IPv4 tail.
  static List<int>? _groups(String text, {required bool last}) {
    if (text.isEmpty) return const [];
    final parts = text.split(':');
    final groups = <int>[];
    for (var index = 0; index < parts.length; index++) {
      final part = parts[index];
      if (part.contains('.')) {
        if (!last || index != parts.length - 1 || !_isDottedQuad(part)) {
          return null;
        }
        final octets = part.split('.').map(int.parse).toList();
        groups
          ..add((octets[0] << 8) | octets[1])
          ..add((octets[2] << 8) | octets[3]);
        continue;
      }
      if (part.isEmpty || part.length > 4) return null;
      var value = 0;
      for (var digit = 0; digit < part.length; digit++) {
        final nibble = _hexDigit(part.codeUnitAt(digit));
        if (nibble < 0) return null;
        value = (value << 4) | nibble;
      }
      groups.add(value);
    }
    return groups;
  }

  /// Four parts of one to three decimal digits, each at most 255, with no
  /// leading zeros.
  static bool _isDottedQuad(String host) {
    final parts = host.split('.');
    if (parts.length != 4) return false;
    for (final part in parts) {
      if (part.isEmpty ||
          part.length > 3 ||
          (part.length > 1 && part.codeUnitAt(0) == 0x30)) {
        return false;
      }
      var value = 0;
      for (var index = 0; index < part.length; index++) {
        final unit = part.codeUnitAt(index);
        if (unit < 0x30 || unit > 0x39) return false;
        value = value * 10 + unit - 0x30;
      }
      if (value > 255) return false;
    }
    return true;
  }

  /// A DNS name of letter-digit-hyphen labels (RFC 1123), lowercased. A name
  /// made only of digits and dots would read as a malformed address, so it
  /// is refused rather than resolved.
  static String? _tryNormalizeName(String host) {
    final units = <int>[];
    var onlyDigitsAndDots = true;
    for (var index = 0; index < host.length; index++) {
      var unit = host.codeUnitAt(index);
      if (unit >= 0x41 && unit <= 0x5A) unit += 0x20;
      final letter = unit >= 0x61 && unit <= 0x7A;
      final digit = unit >= 0x30 && unit <= 0x39;
      if (!letter && !digit && unit != 0x2D && unit != 0x2E) return null;
      if (!digit && unit != 0x2E) onlyDigitsAndDots = false;
      units.add(unit);
    }
    if (onlyDigitsAndDots) return null;
    final name = String.fromCharCodes(units);
    for (final label in name.split('.')) {
      if (label.isEmpty ||
          label.length > 63 ||
          label.startsWith('-') ||
          label.endsWith('-')) {
        return null;
      }
    }
    return name;
  }

  static int _hexDigit(int unit) {
    if (unit >= 0x30 && unit <= 0x39) return unit - 0x30;
    if (unit >= 0x61 && unit <= 0x66) return unit - 0x61 + 10;
    if (unit >= 0x41 && unit <= 0x46) return unit - 0x41 + 10;
    return -1;
  }
}

/// Parses a port of one to five digits without a leading zero, 1 to 65535.
int? _tryParsePort(String text) {
  if (text.isEmpty ||
      text.length > 5 ||
      (text.length > 1 && text.codeUnitAt(0) == 0x30)) {
    return null;
  }
  var port = 0;
  for (var index = 0; index < text.length; index++) {
    final unit = text.codeUnitAt(index);
    if (unit < 0x30 || unit > 0x39) return null;
    port = port * 10 + unit - 0x30;
  }
  return port >= 1 && port <= 65535 ? port : null;
}
