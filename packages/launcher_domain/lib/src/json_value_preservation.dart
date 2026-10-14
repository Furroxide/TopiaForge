import 'dart:convert';

import 'json_duplicate_properties.dart';
import 'json_number_normalization.dart';

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
        normalizedJsonNumber(token) !=
            normalizedJsonNumber(jsonEncode(value))) {
      throw FormatException(
        '$label contains a number at character ${offset + 1} that cannot be preserved without changing its value. No state was written. Repair or explicitly replace that value in the original file before retrying.',
      );
    }
    offset = match.end - 1;
  }
  return decoded;
}

final _jsonNumber = RegExp(
  r'-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?',
);
