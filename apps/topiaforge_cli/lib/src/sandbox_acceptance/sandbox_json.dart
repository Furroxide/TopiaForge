import '../release_strict_json.dart';

/// Bounded, duplicate-rejecting JSON shared by the offline Sandbox contracts.
Map<String, Object?> sandboxDocument(List<int> bytes, String label) {
  final document = decodeReleaseObject(
    bytes,
    maximumBytes: 256 * 1024,
    label: label,
  );
  var nodes = 0;
  void visit(Object? value, int depth) {
    if (++nodes > 12000 || depth > 16) {
      throw StateError('$label exceeds structural bounds.');
    }
    if (value is Map<String, Object?>) {
      for (final entry in value.entries) {
        if (entry.key.length > 128) throw StateError('Oversized property.');
        visit(entry.value, depth + 1);
      }
    } else if (value is List) {
      for (final item in value) {
        visit(item, depth + 1);
      }
    } else if (value is String && value.length > 2048) {
      throw StateError('$label contains oversized text.');
    }
  }

  visit(document, 0);
  return document;
}

Map<String, Object?> sandboxObject(Object? value, String label) {
  if (value is! Map<String, Object?>) {
    throw StateError('$label is not an object.');
  }
  return value;
}

void sandboxFields(
  Map<String, Object?> value,
  Set<String> fields,
  String label,
) {
  if (value.length != fields.length || !value.keys.every(fields.contains)) {
    throw StateError('$label has missing or unknown fields.');
  }
}

String sandboxText(Object? value, String label, {int maximum = 1024}) {
  if (value is! String ||
      value.trim().isEmpty ||
      value.length > maximum ||
      value.runes.any((r) => r < 32 || r == 127)) {
    throw StateError('$label is not bounded nonempty text.');
  }
  return value;
}

String sandboxId(Object? value, String label) {
  final text = sandboxText(value, label, maximum: 80);
  if (!RegExp(r'^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$').hasMatch(text)) {
    throw StateError('$label is not a stable identifier.');
  }
  return text;
}

List<Object?> sandboxList(Object? value, String label, {int maximum = 100}) {
  if (value is! List<Object?> || value.isEmpty || value.length > maximum) {
    throw StateError('$label is not a bounded nonempty list.');
  }
  return value;
}

List<String> sandboxTexts(
  Object? value,
  String label, {
  bool identifiers = false,
}) {
  final result = sandboxList(value, label, maximum: 32)
      .map(
        (item) =>
            identifiers ? sandboxId(item, label) : sandboxText(item, label),
      )
      .toList(growable: false);
  if (result.toSet().length != result.length) {
    throw StateError('$label repeats.');
  }
  return List.unmodifiable(result);
}
