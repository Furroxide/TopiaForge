import 'dart:convert';
import 'json_duplicate_properties.dart';

/// Shared lexical/depth checks for projects and anchors, before their shape readers.
Object? decodeStrictMapJson(String text) {
  try {
    rejectDuplicateJsonProperties(text, label: 'Map');
    final raw = jsonDecode(text);
    var remaining = 2000000;
    void inspect(Object? value, int depth) {
      if (depth > 64 || --remaining < 0) throw const FormatException();
      if (value is double && !value.isFinite) throw const FormatException();
      if (value is String) {
        for (var i = 0; i < value.length; i++) {
          final code = value.codeUnitAt(i);
          if (code >= 0xd800 && code <= 0xdbff) {
            if (++i == value.length ||
                value.codeUnitAt(i) < 0xdc00 ||
                value.codeUnitAt(i) > 0xdfff) {
              throw const FormatException();
            }
          } else if (code >= 0xdc00 && code <= 0xdfff) {
            throw const FormatException();
          }
        }
      }
      if (value is Map) {
        for (final entry in value.entries) {
          inspect(entry.key, depth + 1);
          inspect(entry.value, depth + 1);
        }
      } else if (value is List) {
        for (final item in value) {
          inspect(item, depth + 1);
        }
      }
    }

    inspect(raw, 0);
    return raw;
  } on FormatException {
    throw const FormatException('Invalid or overly complex map JSON.');
  }
}
