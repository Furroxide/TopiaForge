import 'dart:math' as math;

import 'multiplayer.dart';

/// [text] with every token that holds a network address replaced: an IPv4
/// address, an IPv6 address a peer could have, alone, with a port or inside
/// other text, and a name with a port. Host-written text and what the game
/// reports pass through it before the launcher shows or copies them; the
/// only address the launcher shows is the one the player typed, in the
/// dialog they typed it in. Mod ids and server names read as before, colons
/// included: `::` names no one, and "Cafe::Bar" is no address a peer has.
///
/// [maxLength] is for a line its writer cuts, such as the launcher log's:
/// only the words that fit are read, so a host's long text costs no more
/// than a short one, and the cut falls between words, so no word is cut
/// into what reads as an address. A line that was cut ends with '…'.
String withoutAddresses(String text, {int? maxLength}) {
  if (maxLength == null) return _withoutAddresses(text);
  if (maxLength < 1) throw RangeError.range(maxLength, 1, null, 'maxLength');
  final read = _wholeWords(text, maxLength);
  final scrubbed = _withoutAddresses(read);
  if (identical(read, text) && scrubbed.length <= maxLength) return scrubbed;
  // What stands in for an address can be longer than the address, so the
  // words are cut again once scrubbed, with room left for the ellipsis.
  return '${_wholeWords(scrubbed, maxLength - 1)}…';
}

/// The start of [text], at most [maxLength] long, that ends between words:
/// a word the limit would cut is left out whole.
String _wholeWords(String text, int maxLength) {
  if (text.length <= maxLength) return text;
  var end = maxLength;
  while (end > 0 && !_isDelimiter(text.codeUnitAt(end))) {
    end--;
  }
  return text.substring(0, end);
}

String _withoutAddresses(String text) {
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
  // port or a sentence's punctuation. A host can also append a dotted name
  // or hex letters. Read bounded candidates throughout the run, rather than
  // assuming that an address ends where the run ends.
  for (final match in _literalRun.allMatches(token)) {
    final run = match.group(0)!;
    if (!run.contains(':')) continue;
    for (var start = 0; start < run.length; start++) {
      if (!'0236fF:'.contains(run[start])) continue;
      final limit = math.min(run.length, start + _longestCandidate);
      for (var end = limit; end > start + 1; end--) {
        final candidate = run.substring(start, end);
        // A partial hextet must not turn a reserved ::1234 into loopback,
        // and thereby hide ordinary names such as Server::1234.
        final complete = end == run.length || '.:'.contains(run[end]);
        if (_isPeerIPv6(candidate, complete: complete)) return true;
      }
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
  // name. Longer dotted numbers are withheld too: a host can append a
  // numeric label to an address just as easily as a word.
  return RegExp(
    r'(?<![0-9])(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])'
    r'(\.(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}(?![0-9])',
  ).hasMatch(core);
}

/// Hexadecimal digits, colons and dots: what an IPv6 literal is made of.
final _literalRun = RegExp(r'[0-9A-Fa-f:.]+');

/// The longest IPv6 literal, including an embedded dotted IPv4 tail.
const _longestCandidate = 45;

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

/// Whether [text] is an IPv6 address a peer could have, from the ranges in
/// use: global unicast (2000::/3), unique local (fc00::/7), link-local and
/// site-local (fe80::/9), loopback, and those that carry an IPv4 address
/// (::ffff:0:0/96, and NAT64's 64:ff9b::/32). The rest of the space is
/// unassigned, reserved or multicast, and `::` names no one.
bool _isPeerIPv6(String text, {bool complete = true}) {
  if (!text.contains(':') || !_isIpLiteral(text)) return false;
  final address = NetworkHost.tryNormalize(text)!;
  if (!address.contains(':')) return true;
  if (address == '::1') return complete;
  if (address.startsWith('64:ff9b:')) return true;
  if (address.startsWith(':')) return false;
  final group = int.parse(address.split(':').first, radix: 16);
  return (group >= 0x2000 && group <= 0x3fff) ||
      (group & 0xfe00) == 0xfc00 ||
      (group & 0xff80) == 0xfe80;
}
