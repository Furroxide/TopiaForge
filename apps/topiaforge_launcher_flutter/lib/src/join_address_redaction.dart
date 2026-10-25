import 'dart:math' as math;

import 'package:launcher_domain/launcher_domain.dart';

/// [text] with every token that holds a network address replaced: an IPv4
/// address, an IPv6 address a peer could have, alone, with a port or inside
/// other text, and a name with a port. Host-written text and what the game
/// reports pass through it before the launcher shows or copies them; the
/// only address the launcher shows is the one the player typed, in the
/// dialog they typed it in. Mod ids and server names read as before, colons
/// included: `::` names no one, and "Cafe::Bar" is no address a peer has.
String withoutAddresses(String text) {
  if (text.isEmpty) return text;
  final out = StringBuffer();
  var index = 0;
  while (index < text.length) {
    if (_isDelimiter(text.codeUnitAt(index))) {
      out.writeCharCode(text.codeUnitAt(index));
      index++;
      continue;
    }
    final start = index;
    while (index < text.length && !_isDelimiter(text.codeUnitAt(index))) {
      index++;
    }
    final token = text.substring(start, index);
    out.write(_holdsAddress(token) ? withheldAddress : token);
  }
  return out.toString();
}

/// What stands in for an address [withoutAddresses] took out.
const withheldAddress = '[address]';

bool _isDelimiter(int unit) =>
    unit <= 0x20 || '"\'`()<>{}[],;|'.codeUnits.contains(unit);

bool _holdsAddress(String token) {
  // An IPv6 address can sit inside a token, which a host writes too: after a
  // label such as `ip=`, `peer:` or `v1.2:`, or before a zone, a path, a
  // port or a sentence's punctuation. Each run of literal characters is read
  // from its start and from just after each colon or dot in it. An address,
  // a port and the punctuation after them fit in the end of a run, so only
  // that is read, and a long run stays cheap.
  for (final match in _literalRun.allMatches(token)) {
    final run = match.group(0)!;
    if (!run.contains(':')) continue;
    final first = math.max(0, run.length - _longestCandidate);
    for (var start = first; start < run.length; start++) {
      if (start > 0 && !'.:'.contains(run[start - 1])) continue;
      if (_holdsPeerIPv6(run.substring(start))) return true;
    }
  }
  var core = token;
  while (core.isNotEmpty && '.!?:'.contains(core[core.length - 1])) {
    core = core.substring(0, core.length - 1);
  }
  if (core.isEmpty) return false;
  if (_isAddress(core)) return true;
  // A host with a port: an address or a dotted name before the last colon.
  final colon = core.lastIndexOf(':');
  if (colon > 0 && colon < core.length - 1) {
    final port = core.substring(colon + 1);
    final host = core.substring(0, colon);
    if (_port.hasMatch(port) &&
        (_isAddress(host) ||
            host == 'localhost' ||
            RegExp(r'^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$').hasMatch(host) &&
                RegExp('[A-Za-z]').hasMatch(host))) {
      return true;
    }
  }
  // An IPv4 address glued to other text, such as a word, a path or a dotted
  // name, but not four parts of a longer dotted number, such as a version.
  return RegExp(
    r'(?<![0-9])(?<![0-9]\.)(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])'
    r'(\.(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}(?![0-9])(?!\.[0-9])',
  ).hasMatch(core);
}

/// Hexadecimal digits, colons and dots: what an IPv6 literal is made of.
final _literalRun = RegExp(r'[0-9A-Fa-f:.]+');

/// The longest IPv6 literal with a port, and punctuation after them.
const _longestCandidate = 64;

final _port = RegExp(r'^[0-9]{1,5}$');

/// Whether [text] is an IPv4 address, or an IPv6 address a peer could have.
bool _isAddress(String text) =>
    text.contains(':') ? _isPeerIPv6(text) : _isIpLiteral(text);

bool _isIpLiteral(String text) {
  try {
    return NetworkHost.pathKindOf(text) != null;
  } on ArgumentError {
    return false;
  }
}

/// Whether [text] is an IPv6 address a peer could have, alone or followed by
/// a port, or by the dots and colons that end a sentence or a label.
bool _holdsPeerIPv6(String text) {
  var end = text.length;
  while (end > 1) {
    final literal = text.substring(0, end);
    if (_isPeerIPv6(literal)) return true;
    final port = literal.lastIndexOf(':');
    if (port > 0 &&
        _port.hasMatch(literal.substring(port + 1)) &&
        _isPeerIPv6(literal.substring(0, port))) {
      return true;
    }
    if (!'.:'.contains(text[end - 1])) return false;
    end--;
  }
  return false;
}

/// Whether [text] is an IPv6 address a peer could have, from the ranges in
/// use: global unicast (2000::/3), unique local (fc00::/7), link-local and
/// site-local (fe80::/9), loopback, and those that carry an IPv4 address
/// (::ffff:0:0/96, and NAT64's 64:ff9b::/32). The rest of the space is
/// unassigned, reserved or multicast, and `::` names no one.
bool _isPeerIPv6(String text) {
  if (!text.contains(':') || !_isIpLiteral(text)) return false;
  final address = NetworkHost.tryNormalize(text)!;
  if (!address.contains(':')) return true;
  if (address == '::1' || address.startsWith('64:ff9b:')) return true;
  if (address.startsWith(':')) return false;
  final group = int.parse(address.split(':').first, radix: 16);
  return (group >= 0x2000 && group <= 0x3fff) ||
      (group & 0xfe00) == 0xfc00 ||
      (group & 0xff80) == 0xfe80;
}
