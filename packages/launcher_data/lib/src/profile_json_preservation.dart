import 'dart:convert';

import 'json_duplicate_properties.dart';

/// Refuses numeric JSON values that would change during persistence or migration.
/// Equivalent decimal spellings may change; quoted values are never inspected.
Object? decodeJsonPreservingValues(String text, {required String label}) {
  rejectDuplicateJsonProperties(text, label: label);
  final decoded = jsonDecode(text);
  var quoted = false;
  var escaped = false;
  for (var offset = 0; offset < text.length; offset++) {
    final code = text.codeUnitAt(offset);
    if (quoted) {
      if (escaped) {
        escaped = false;
      } else if (code == 92) {
        escaped = true;
      } else if (code == 34) {
        quoted = false;
      }
      continue;
    }
    if (code == 34) {
      quoted = true;
      continue;
    }
    if (code != 45 && (code < 48 || code > 57)) continue;
    final match = _jsonNumber.matchAsPrefix(text, offset)!;
    final token = match.group(0)!;
    final value = jsonDecode(token) as num;
    if (!value.isFinite ||
        _decimalValue(token) != _decimalValue(jsonEncode(value))) {
      throw FormatException(
        '$label contains a number at character ${offset + 1} that cannot be preserved without changing its value. No profile was written. Repair or explicitly replace that value in the original file before retrying.',
      );
    }
    offset = match.end - 1;
  }
  return decoded;
}

final _jsonNumber = RegExp(
  r'-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?',
);

String _decimalValue(String token) {
  final negative = token.startsWith('-');
  final unsigned = negative ? token.substring(1) : token;
  final parts = unsigned.toLowerCase().split('e');
  final mantissa = parts.first;
  final dot = mantissa.indexOf('.');
  var digits = mantissa.replaceAll('.', '').replaceFirst(RegExp(r'^0+'), '');
  if (digits.isEmpty) return '0';
  var exponent = parts.length == 1 ? BigInt.zero : BigInt.parse(parts.last);
  if (dot >= 0) exponent -= BigInt.from(mantissa.length - dot - 1);
  final suffix = RegExp(r'0+$').firstMatch(digits);
  if (suffix != null) {
    exponent += BigInt.from(digits.length - suffix.start);
    digits = digits.substring(0, suffix.start);
  }
  return '${negative ? '-' : ''}$digits:$exponent';
}
