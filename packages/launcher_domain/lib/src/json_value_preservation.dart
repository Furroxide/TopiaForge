import 'dart:convert';

import 'json_duplicate_properties.dart';
import 'json_number_normalization.dart';

/// Refuses numeric JSON values that would change during persistence or migration.
/// Equivalent decimal spellings may change; quoted values are never inspected.
///
/// Each number is compared with the value that decoding the whole document gave
/// it, found by walking the decoded arrays and objects along with the text.
/// Decoding the number alone is not enough. Inside a document, Dart's decoder
/// turns a number with an exponent above 400 or below -400 into infinity or
/// zero, whatever digits precede the exponent, so `1<100 zeros>e-401` decodes
/// to 0.0 there but to 1e-301 alone.
Object? decodeJsonPreservingValues(String text, {required String label}) {
  rejectDuplicateJsonProperties(text, label: label);
  final decoded = jsonDecode(text);
  // The decoded arrays and objects around the scan position, innermost last.
  final containers = <_DecodedContainer>[];
  Object? scanned() => containers.isEmpty ? decoded : containers.last.member;
  var quoted = false;
  var escaped = false;
  var quoteStart = 0;
  for (var offset = 0; offset < text.length; offset++) {
    final code = text.codeUnitAt(offset);
    if (quoted) {
      if (escaped) {
        escaped = false;
      } else if (code == 92) {
        escaped = true;
      } else if (code == 34) {
        quoted = false;
        if (containers.lastOrNull?.awaitsName ?? false) {
          containers.last.name =
              jsonDecode(text.substring(quoteStart, offset + 1)) as String;
        }
      }
      continue;
    }
    if (code == 34) {
      quoted = true;
      quoteStart = offset;
      continue;
    }
    if (code == 123 || code == 91) {
      containers.add(_DecodedContainer(scanned()));
      continue;
    }
    if (code == 125 || code == 93) {
      containers.removeLast();
      continue;
    }
    if (code == 44) {
      containers.last.advance();
      continue;
    }
    if (code != 45 && (code < 48 || code > 57)) continue;
    // The text decoded, so the number ends at the first code unit that no
    // number holds. A pattern would push a backtracking entry for each digit
    // of a fraction or exponent, and overflow the stack on a 4 MiB number.
    var end = offset + 1;
    while (end < text.length && _isNumberPart(text.codeUnitAt(end))) {
      end++;
    }
    final token = text.substring(offset, end);
    final value = scanned() as num;
    if (!value.isFinite ||
        normalizedJsonNumber(token) !=
            normalizedJsonNumber(jsonEncode(value))) {
      throw FormatException(
        '$label contains a number at character ${offset + 1} that cannot be preserved without changing its value. No state was written. Repair or explicitly replace that value in the original file before retrying.',
      );
    }
    offset = end - 1;
  }
  return decoded;
}

/// Whether a JSON number can hold [code]: a digit, `.`, `e`, `E`, `+` or
/// `-`. No other code unit can follow a number in a document that decoded.
bool _isNumberPart(int code) =>
    code >= 48 && code <= 57 ||
    code == 46 ||
    code == 101 ||
    code == 69 ||
    code == 43 ||
    code == 45;

/// A decoded array or object, with the index or name of the member the scan is
/// in. Members are looked up by name rather than by position, so the walk does
/// not depend on the order a decoded object keeps; duplicate names were refused.
final class _DecodedContainer {
  _DecodedContainer(this._value);

  final Object? _value;
  var _index = 0;
  String? _name;

  /// Whether the next string the scan reaches is a member name.
  bool get awaitsName => _value is Map && _name == null;

  set name(String name) => _name = name;

  /// The decoded value of the member the scan is in.
  Object? get member =>
      _value is List ? _value[_index] : (_value as Map)[_name];

  /// Moves to the next member, past a comma.
  void advance() {
    _index++;
    _name = null;
  }
}
