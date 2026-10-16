/// Linear-time scanners for the project, props, and Steam files the launcher
/// reads.
///
/// Each scanner replaces one backtracking [RegExp] and returns exactly what
/// that pattern matched, including which occurrence wins, so well-formed
/// files read the same. On malformed files those patterns took quadratic or
/// cubic time; these scanners examine each code unit a bounded number of
/// times. `test/linear_text_scan_test.dart` checks each scanner against the
/// pattern it replaced.
library;

const _quote = 0x22;
const _apostrophe = 0x27;
const _slash = 0x2F;
const _greaterThan = 0x3E;
const _backslash = 0x5C;

/// Whether `\s` in a non-Unicode [RegExp] matches the UTF-16 [codeUnit].
///
/// That is ECMAScript white space plus line terminators. Unlike
/// [String.trim], it does not include U+0085.
bool isRegExpWhitespace(int codeUnit) => switch (codeUnit) {
  >= 0x09 && <= 0x0D ||
  0x20 ||
  0xA0 ||
  0x1680 ||
  >= 0x2000 && <= 0x200A ||
  0x2028 ||
  0x2029 ||
  0x202F ||
  0x205F ||
  0x3000 ||
  0xFEFF => true,
  _ => false,
};

/// The trimmed text of the first `<name>…</name>` element, as
/// `<name>\s*([^<]+?)\s*</name>` captured it, or null.
///
/// An element is skipped when its text is empty or when the next `<` does
/// not start `</name>`. Text that is only whitespace yields its last code
/// unit, as the lazy group did. [name] is matched literally.
String? firstElementText(String text, String name) {
  final open = '<$name>';
  final close = '</$name>';
  var start = text.indexOf(open);
  while (start >= 0) {
    final from = start + open.length;
    final to = text.indexOf('<', from);
    if (to < 0) return null;
    if (to > from && text.startsWith(close, to)) {
      var first = from;
      while (first < to && isRegExpWhitespace(text.codeUnitAt(first))) {
        first++;
      }
      if (first == to) return text.substring(to - 1, to);
      var last = to;
      while (isRegExpWhitespace(text.codeUnitAt(last - 1))) {
        last--;
      }
      return text.substring(first, last);
    }
    // No other element can open before the `<` that ended this one.
    start = text.indexOf(open, to);
  }
  return null;
}

/// The bounds of the first `<name>` or `<name …>` start tag at or after
/// [start], as `<name(?:\s[^>]*)?>` matched it, or null.
///
/// A tag with attributes ends at the first `>` after its name, wherever that
/// is. [name] is matched literally.
({int start, int end})? firstStartTag(
  String text,
  String name, {
  int start = 0,
}) {
  final open = '<$name';
  var index = text.indexOf(open, start);
  while (index >= 0) {
    final next = index + open.length;
    if (next == text.length) return null;
    final unit = text.codeUnitAt(next);
    if (unit == _greaterThan) return (start: index, end: next + 1);
    if (isRegExpWhitespace(unit)) {
      // Every later tag needs a `>` too, so a missing one ends the search.
      final close = text.indexOf('>', next + 1);
      return close < 0 ? null : (start: index, end: close + 1);
    }
    index = text.indexOf(open, index + 1);
  }
  return null;
}

/// The bounds of the first `<name …>text</name>` element whose text holds no
/// `<`, as `<name(?:\s[^>]*)?>[^<]*</name>` matched it, or null.
///
/// [name] is matched literally.
({int start, int end})? firstTextElement(String text, String name) {
  final close = '</$name>';
  var from = 0;
  while (true) {
    final tag = firstStartTag(text, name, start: from);
    if (tag == null) return null;
    final textEnd = text.indexOf('<', tag.end);
    if (textEnd < 0) return null;
    if (text.startsWith(close, textEnd)) {
      return (start: tag.start, end: textEnd + close.length);
    }
    // A tag that opens before [textEnd] ends at the same `>`, so it fails the
    // same way.
    from = textEnd;
  }
}

/// The bounds of the first `PackageReference` item for [package], as
/// `<PackageReference\s+Include=["']package["'][^>]*(?:/>|>.*?</PackageReference>)`
/// matched it with `dotAll`, or null.
///
/// Like that pattern, a self-closing item runs on to the next
/// `</PackageReference>` when one follows anywhere later in [text]. [package]
/// is a NuGet id, matched literally; NuGet ids never contain `<` or `>`.
({int start, int end})? firstPackageReference(String text, String package) {
  const open = '<PackageReference';
  const close = '</PackageReference>';
  var closeMissing = false;
  var start = text.indexOf(open);
  while (start >= 0) {
    final attributes = _afterInclude(text, start + open.length, package);
    if (attributes < 0) {
      start = text.indexOf(open, start + 1);
      continue;
    }
    final tagEnd = text.indexOf('>', attributes);
    // Every later item needs a `>` too, so a missing one ends the search.
    if (tagEnd < 0) return null;
    if (!closeMissing) {
      final closeStart = text.indexOf(close, tagEnd + 1);
      if (closeStart >= 0) {
        return (start: start, end: closeStart + close.length);
      }
      closeMissing = true;
    }
    if (tagEnd > attributes && text.codeUnitAt(tagEnd - 1) == _slash) {
      return (start: start, end: tagEnd + 1);
    }
    // An item that opens before [tagEnd] shares its `>`, so it fails the
    // same way.
    start = text.indexOf(open, tagEnd);
  }
  return null;
}

/// The offset just after `\s+Include=["']package["']` at [index], or -1.
int _afterInclude(String text, int index, String package) {
  const include = 'Include=';
  var cursor = index;
  while (cursor < text.length && isRegExpWhitespace(text.codeUnitAt(cursor))) {
    cursor++;
  }
  if (cursor == index || !text.startsWith(include, cursor)) return -1;
  final opening = cursor + include.length;
  final closing = opening + 1 + package.length;
  if (closing >= text.length ||
      !_isQuote(text.codeUnitAt(opening)) ||
      !text.startsWith(package, opening + 1) ||
      !_isQuote(text.codeUnitAt(closing))) {
    return -1;
  }
  return closing + 1;
}

bool _isQuote(int codeUnit) => codeUnit == _quote || codeUnit == _apostrophe;

/// The quoted key/value pairs in Steam VDF [text], still escaped, as
/// `allMatches` of `"((?:\\.|[^"\\])*)"\s*"((?:\\.|[^"\\])*)"` captured them.
Iterable<(String, String)> vdfQuotedPairs(String text) sync* {
  var start = text.indexOf('"');
  while (start >= 0) {
    final keyEnd = _vdfStringEnd(text, start + 1);
    if (keyEnd == text.length || text.codeUnitAt(keyEnd) != _quote) {
      // Any quote before [keyEnd] is escaped, so a key opened there runs to
      // [keyEnd] too and stays unclosed.
      start = keyEnd == text.length ? -1 : text.indexOf('"', keyEnd + 1);
      continue;
    }
    var valueStart = keyEnd + 1;
    while (valueStart < text.length &&
        isRegExpWhitespace(text.codeUnitAt(valueStart))) {
      valueStart++;
    }
    if (valueStart < text.length && text.codeUnitAt(valueStart) == _quote) {
      final valueEnd = _vdfStringEnd(text, valueStart + 1);
      if (valueEnd < text.length && text.codeUnitAt(valueEnd) == _quote) {
        yield (
          text.substring(start + 1, keyEnd),
          text.substring(valueStart + 1, valueEnd),
        );
        start = text.indexOf('"', valueEnd + 1);
        continue;
      }
    }
    // A key opened at an escaped quote fails the same way, but the closing
    // quote of this key can still open a pair.
    start = keyEnd;
  }
}

/// Where the body of a quoted VDF string that starts at [index] stops: at
/// its closing quote, at a backslash that escapes nothing, or at the end.
int _vdfStringEnd(String text, int index) {
  var cursor = index;
  while (cursor < text.length) {
    final unit = text.codeUnitAt(cursor);
    if (unit == _quote) return cursor;
    if (unit != _backslash) {
      cursor++;
    } else if (cursor + 1 < text.length &&
        !_isLineTerminator(text.codeUnitAt(cursor + 1))) {
      // `\\.` escapes any code unit but a line terminator.
      cursor += 2;
    } else {
      return cursor;
    }
  }
  return cursor;
}

bool _isLineTerminator(int codeUnit) =>
    codeUnit == 0x0A ||
    codeUnit == 0x0D ||
    codeUnit == 0x2028 ||
    codeUnit == 0x2029;
