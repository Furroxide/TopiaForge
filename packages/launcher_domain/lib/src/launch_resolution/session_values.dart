part of '../launch_resolution.dart';

// Shared readers for the launch-session documents: request V5, progress and
// outcome V2, and the session channels.
//
// Every one of them is written as sorted JSON: each object's keys in ordinal
// order, no white space, and the minimal escapes `jsonEncode` writes (the
// quotation mark, the backslash and U+0000 to U+001F). The game, the launcher
// and the fixture generator therefore write the same bytes. Readers accept
// any member order and spacing, but refuse unknown or repeated members,
// explicit nulls and wrong types, like the V4 readers.
//
// No refusal repeats the value it refused: a value may be a host address.

/// The byte bound of a session channel or preferences document.
const _channelDocumentBytes = 64 * 1024;

/// [value] with every map rebuilt in ordinal key order, which `jsonEncode`
/// then writes as it iterates.
Object? _sorted(Object? value) {
  if (value is Map<String, Object?>) {
    final keys = value.keys.toList()..sort();
    return <String, Object?>{for (final key in keys) key: _sorted(value[key])};
  }
  if (value is List) return [for (final item in value) _sorted(item)];
  return value;
}

/// Display text of [minimum] to [maximum] UTF-16 code units, as the wire
/// formats count them, without the noncharacters U+FFFE and U+FFFF: the
/// game's JSON reader refuses them, so a document holding one could never be
/// read back.
String _displayText(Object? value, int maximum, {int minimum = 1}) {
  if (value is! String ||
      !WireText.isDisplayText(value, maximum, minimum: minimum) ||
      value.contains('\uFFFE') ||
      value.contains('\uFFFF')) {
    throw const FormatException('Invalid session display text.');
  }
  return value;
}

/// A server id, descriptor hash or package digest: 64 lowercase hexadecimal
/// digits.
String _sha256Hex(Object? value) {
  if (value is! String || !WireText.isHex(value, 32)) {
    throw const FormatException('Invalid session hash.');
  }
  return value;
}

/// A preferences journal's epoch: 32 lowercase hexadecimal digits, the
/// random id the game gives a journal it starts.
String _journalEpoch(Object? value) {
  if (value is! String || !WireText.isHex(value, 16)) {
    throw const FormatException('Invalid journal epoch.');
  }
  return value;
}

/// An integer from [minimum] to [maximum].
int _ranged(Object? value, int minimum, int maximum) {
  final number = _integer(value);
  if (number < minimum || number > maximum) {
    throw const FormatException('Session number out of range.');
  }
  return number;
}

String _inviteCode(Object? value) {
  if (value is! String || !InviteCode.isCanonical(value)) {
    throw const FormatException('Invalid invite code.');
  }
  return value;
}

/// A control-plane origin in the one spelling a launch carries: `https://`, a
/// lowercase DNS name, and a port of 1 to 65535 in plain decimal, never the
/// default 443. The service is not a peer, but an address is refused all the
/// same, so none can pass as one: an IP literal, and a name whose last label
/// is a number, decimal or `0x` hexadecimal, which the URL Standard and the
/// system resolver read as an IPv4 address (`0x7f000001` is 127.0.0.1).
String _serviceOrigin(Object? value) {
  const invalid = FormatException('Invalid service origin.');
  if (value is! String || value.length > 256 || !value.startsWith('https://')) {
    throw invalid;
  }
  final authority = value.substring('https://'.length);
  final colon = authority.indexOf(':');
  final host = colon < 0 ? authority : authority.substring(0, colon);
  if (colon >= 0) {
    final port = _portText(authority.substring(colon + 1));
    if (port == null || port == 443) throw invalid;
  }
  if (host.isEmpty ||
      NetworkHost.tryNormalize(host) != host ||
      NetworkHost.pathKindOf(host) != null ||
      _endsInNumber(host)) {
    throw invalid;
  }
  return value;
}

/// Whether the last label of the DNS name [host] is a number: all decimal
/// digits, or `0x` followed by hexadecimal digits only.
bool _endsInNumber(String host) {
  final last = host.substring(host.lastIndexOf('.') + 1);
  final digits = last.startsWith('0x') ? last.substring(2) : last;
  final hex = digits.length != last.length;
  return digits.codeUnits.every(
    (unit) =>
        (unit >= 0x30 && unit <= 0x39) || (hex && unit >= 0x61 && unit <= 0x66),
  );
}

/// A direct target's host, in the one spelling [NetworkHost] writes.
String _directHost(Object? value) {
  if (value is! String || NetworkHost.tryNormalize(value) != value) {
    throw const FormatException('Invalid direct host.');
  }
  return value;
}

/// Whether contacting [host] stays on the local network: a private,
/// link-local, unique-local or loopback IP literal, or a name that resolves
/// without asking anyone beyond it.
bool _isLocalNetworkHost(String host) =>
    NetworkHost.pathKindOf(host) == NetworkPathKind.lan ||
    NetworkHost.isLocalName(host);

/// A port of one to five digits without a leading zero, 1 to 65535.
int? _portText(String text) {
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

/// The wire name of [value] among [values], refusing anything else.
T _named<T extends Enum>(
  Object? value,
  List<T> values,
  String Function(T) name,
) {
  final text = _text(value, max: 64);
  for (final item in values) {
    if (name(item) == text) return item;
  }
  throw const FormatException('Unknown session value.');
}

/// `camelCase` enum names as `kebab-case` wire names.
String _kebab(Enum value) => value.name.replaceAllMapped(
  RegExp('[A-Z]'),
  (match) => '-${match[0]!.toLowerCase()}',
);
