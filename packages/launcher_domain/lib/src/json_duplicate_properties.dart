import 'dart:convert';

/// Examines raw property tokens before a JSON decoder can collapse duplicates.
/// Syntax validation remains the decoder's job; this pass retains object scopes.
void rejectDuplicateJsonProperties(String text, {required String label}) {
  final scopes = <Set<String>?>[];
  for (var offset = 0; offset < text.length; offset++) {
    final code = text.codeUnitAt(offset);
    if (code == 123) {
      if (scopes.length >= 128) {
        throw FormatException('$label exceeds 128 JSON nesting levels.');
      }
      scopes.add(<String>{});
      continue;
    }
    if (code == 91) {
      if (scopes.length >= 128) {
        throw FormatException('$label exceeds 128 JSON nesting levels.');
      }
      scopes.add(null);
      continue;
    }
    if (code == 125 || code == 93) {
      if (scopes.isNotEmpty) scopes.removeLast();
      continue;
    }
    if (code != 34) continue;
    final start = offset;
    var escaped = false;
    var closed = false;
    while (++offset < text.length) {
      final next = text.codeUnitAt(offset);
      if (escaped) {
        escaped = false;
        continue;
      }
      if (next == 92) {
        escaped = true;
        continue;
      }
      if (next == 34) {
        closed = true;
        break;
      }
    }
    if (!closed) {
      throw FormatException('$label contains an unterminated JSON string.');
    }
    var next = offset + 1;
    while (next < text.length &&
        const [9, 10, 13, 32].contains(text.codeUnitAt(next))) {
      next++;
    }
    if (next >= text.length || text.codeUnitAt(next) != 58) continue;
    final key = jsonDecode(text.substring(start, offset + 1)) as String;
    if (scopes.isNotEmpty && scopes.last != null && !scopes.last!.add(key)) {
      throw FormatException(
        '$label contains duplicate property ${jsonEncode(key)} at character ${start + 1}. Repair the original file; no state was written.',
      );
    }
  }
}
