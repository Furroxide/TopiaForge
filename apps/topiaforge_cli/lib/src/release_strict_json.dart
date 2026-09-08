import 'dart:convert';

import 'package:json_schema/json_schema.dart';

Map<String, Object?> decodeReleaseObject(
  List<int> bytes, {
  required int maximumBytes,
  required String label,
}) {
  if (bytes.isEmpty || bytes.length > maximumBytes) {
    throw StateError('$label has an invalid size.');
  }
  try {
    final text = utf8.decode(bytes, allowMalformed: false);
    _UniqueJsonProperties(text).validate();
    final value = jsonDecode(text);
    if (value is! Map<String, Object?>) {
      throw const FormatException('Expected one object.');
    }
    return value;
  } on FormatException catch (error) {
    throw StateError('$label is not strict UTF-8 JSON: $error');
  }
}

void validateReleaseSchema(
  Map<String, Object?> document,
  Map<String, Object?> schema,
  String label,
) {
  final result = JsonSchema.create(schema).validate(document);
  if (!result.isValid) {
    throw StateError('$label is schema-invalid: ${result.errors.join('; ')}');
  }
}

/// Deterministic JSON for comparisons; ordering of arrays remains meaningful.
String canonicalReleaseJson(Object? value) => jsonEncode(_canonical(value));
Object? _canonical(Object? value) {
  if (value is Map<String, Object?>) {
    final keys = value.keys.toList()..sort();
    return {for (final key in keys) key: _canonical(value[key])};
  }
  if (value is List) return value.map(_canonical).toList();
  return value;
}

class _UniqueJsonProperties {
  _UniqueJsonProperties(this.source);
  final String source;
  int position = 0;
  void validate() {
    _value(0);
    _space();
    if (position != source.length) _invalid();
  }

  void _invalid() => throw const FormatException(
    'Invalid or duplicate release JSON property.',
  );
  void _space() {
    while (position < source.length &&
        const [9, 10, 13, 32].contains(source.codeUnitAt(position))) {
      position++;
    }
  }

  bool _take(String char) {
    _space();
    if (position < source.length && source[position] == char) {
      position++;
      return true;
    }
    return false;
  }

  void _need(String char) {
    if (!_take(char)) _invalid();
  }

  void _value(int depth) {
    if (depth > 128) _invalid();
    _space();
    if (position >= source.length) _invalid();
    if (_take('{')) {
      final seen = <String>{};
      if (_take('}')) return;
      do {
        _space();
        final key = _string();
        if (!seen.add(key)) _invalid();
        _need(':');
        _value(depth + 1);
        if (_take('}')) return;
        _need(',');
      } while (true);
    }
    if (_take('[')) {
      if (_take(']')) return;
      do {
        _value(depth + 1);
        if (_take(']')) return;
        _need(',');
      } while (true);
    }
    if (source[position] == '"') {
      _string();
      return;
    }
    final start = position;
    while (position < source.length &&
        !',]} \t\r\n'.contains(source[position])) {
      position++;
    }
    if (start == position) _invalid();
    jsonDecode(source.substring(start, position));
  }

  String _string() {
    final start = position;
    _need('"');
    while (position < source.length) {
      final char = source[position++];
      if (char == '"') {
        return jsonDecode(source.substring(start, position)) as String;
      }
      if (char == '\\') position++;
    }
    _invalid();
    return '';
  }
}
