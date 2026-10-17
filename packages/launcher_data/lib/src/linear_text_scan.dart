/// Linear-time scanners for the project, props, and Steam files the launcher
/// reads.
///
/// Most scanners replace one backtracking [RegExp] and return exactly what
/// that pattern matched, including which occurrence wins, so well-formed
/// files read the same. On malformed files those patterns took quadratic or
/// cubic time; these scanners examine each code unit a bounded number of
/// times. `test/linear_text_scan_test.dart` checks each scanner against the
/// pattern it replaced.
///
/// [readStartTag], [startTags] and [packageReferences] read start tags the
/// way XML writes them instead: attributes in any order, white space around
/// `=`, values in either quote, and comments and CDATA sections skipped.
/// `test/start_tag_scan_test.dart` checks the package items they find
/// against a pattern written to that grammar.
library;

const _quote = 0x22;
const _apostrophe = 0x27;
const _slash = 0x2F;
const _lessThan = 0x3C;
const _equals = 0x3D;
const _greaterThan = 0x3E;
const _backslash = 0x5C;

/// One attribute of a start tag that [readStartTag] read, as offsets into
/// its text. The white space before the attribute starts at `leading`, and
/// its name runs from `start` to `nameEnd`. Its value runs from its opening
/// quote at `valueStart` to just past its closing quote at `end`.
typedef TagAttribute = ({
  int leading,
  int start,
  int nameEnd,
  int valueStart,
  int end,
});

/// A start tag that [readStartTag] read, as offsets into its text. Its
/// element name ends at `nameEnd`, and its last attribute (or its name,
/// without any) at `attributesEnd`. The tag itself ends just past its `>`,
/// at `end`.
typedef StartTag = ({
  int nameEnd,
  List<TagAttribute> attributes,
  int attributesEnd,
  int end,
});

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

/// The start tag that opens at the `<` at [start] in [text], or null when
/// it does not read as XML writes one.
///
/// After the element name, each attribute has white space before it, then
/// a name, `=` with optional white space around it, and a value in either
/// quote. The tag ends at the first `/>` or `>` outside a value. A value
/// that does not close or holds a `<`, which XML forbids, makes the tag
/// unreadable, as does any other text. Reading stops at the next `<` at the
/// latest, so reading every tag in a file takes linear time.
StartTag? readStartTag(String text, int start) {
  var cursor = start + 1;
  while (cursor < text.length && !_endsName(text.codeUnitAt(cursor))) {
    cursor++;
  }
  final nameEnd = cursor;
  final attributes = <TagAttribute>[];
  while (true) {
    final leading = cursor;
    cursor = _skipWhitespace(text, cursor);
    if (cursor == text.length) return null;
    final ends = text.codeUnitAt(cursor) == _greaterThan
        ? cursor + 1
        : text.startsWith('/>', cursor)
        ? cursor + 2
        : -1;
    if (ends >= 0) {
      return (
        nameEnd: nameEnd,
        attributes: attributes,
        attributesEnd: leading,
        end: ends,
      );
    }
    // XML separates attributes with white space.
    if (cursor == leading) return null;
    final nameStart = cursor;
    while (cursor < text.length &&
        !_endsAttributeName(text.codeUnitAt(cursor))) {
      cursor++;
    }
    final attributeNameEnd = cursor;
    if (attributeNameEnd == nameStart) return null;
    cursor = _skipWhitespace(text, cursor);
    if (cursor == text.length || text.codeUnitAt(cursor) != _equals) {
      return null;
    }
    cursor = _skipWhitespace(text, cursor + 1);
    if (cursor == text.length) return null;
    final quote = text.codeUnitAt(cursor);
    if (quote != _quote && quote != _apostrophe) return null;
    final valueStart = cursor;
    do {
      cursor++;
      if (cursor == text.length || text.codeUnitAt(cursor) == _lessThan) {
        return null;
      }
    } while (text.codeUnitAt(cursor) != quote);
    cursor++;
    attributes.add((
      leading: leading,
      start: nameStart,
      nameEnd: attributeNameEnd,
      valueStart: valueStart,
      end: cursor,
    ));
  }
}

/// The end of the comment or CDATA section that opens at the `<` at [open]
/// in [text], whose text is not markup, -1 when it does not close, which
/// hides the rest of [text], or null when neither opens there.
int? unparsedSectionEnd(String text, int open) {
  for (final (opener, closer) in const [
    ('<!--', '-->'),
    ('<![CDATA[', ']]>'),
  ]) {
    if (!text.startsWith(opener, open)) continue;
    final close = text.indexOf(closer, open + opener.length);
    return close < 0 ? -1 : close + closer.length;
  }
  return null;
}

/// The start tags in [text] whose element name is [name], outside comments
/// and CDATA sections and in order, each with the offset of its `<`.
///
/// Each is read by [readStartTag], and one it cannot read is skipped. With
/// [anyCase], the name matches in any ASCII case, as MSBuild matches item
/// types. [name] holds only ASCII letters. The search begins at [start],
/// which must not be inside a comment or CDATA section.
Iterable<({int start, StartTag tag})> startTags(
  String text,
  String name, {
  bool anyCase = false,
  int start = 0,
}) sync* {
  var open = text.indexOf('<', start);
  while (open >= 0) {
    final unparsedEnd = unparsedSectionEnd(text, open);
    if (unparsedEnd != null) {
      if (unparsedEnd < 0) return;
      open = text.indexOf('<', unparsedEnd);
      continue;
    }
    if (_isNameAt(text, open + 1, name, anyCase)) {
      final tag = readStartTag(text, open);
      if (tag != null) yield (start: open, tag: tag);
    }
    open = text.indexOf('<', open + 1);
  }
}

/// Whether [attribute] of a start tag in [text] is named exactly [name].
bool isAttributeNamed(String text, TagAttribute attribute, String name) =>
    attribute.nameEnd - attribute.start == name.length &&
    text.startsWith(name, attribute.start);

/// The bounds of [attribute]'s value in [text], inside its quotes and
/// without the white space at either end.
({int start, int end}) trimmedAttributeValue(
  String text,
  TagAttribute attribute,
) {
  var start = attribute.valueStart + 1;
  var end = attribute.end - 1;
  while (start < end && isRegExpWhitespace(text.codeUnitAt(start))) {
    start++;
  }
  while (end > start && isRegExpWhitespace(text.codeUnitAt(end - 1))) {
    end--;
  }
  return (start: start, end: end);
}

/// The `PackageReference` items in [text] from [start] on, outside comments
/// and CDATA sections and in order, each with its bounds and its start tag.
///
/// The items are the start tags that [startTags] finds, with their element
/// name in any ASCII case. A self-closing item ends at its own `/>`. An
/// element item ends at the first `</PackageReference>` after its start
/// tag, in any case and outside comments and CDATA sections, with optional
/// white space before the `>`; one with no such end tag is skipped. [start]
/// must not be inside a comment or CDATA section.
Iterable<({int start, int end, StartTag tag})> packageReferences(
  String text, {
  int start = 0,
}) sync* {
  const name = 'PackageReference';
  // The end tag found last. A search from before it would find it again.
  ({int start, int end})? endTag;
  var endTagMissing = false;
  for (final found in startTags(text, name, anyCase: true, start: start)) {
    final tag = found.tag;
    if (text.codeUnitAt(tag.end - 2) == _slash) {
      yield (start: found.start, end: tag.end, tag: tag);
      continue;
    }
    // No later element item can close either.
    if (endTagMissing) continue;
    if (endTag == null || tag.end > endTag.start) {
      endTag = _endTag(text, name, tag.end);
      if (endTag == null) {
        endTagMissing = true;
        continue;
      }
    }
    yield (start: found.start, end: endTag.end, tag: tag);
  }
}

/// The bounds of the first `</name>` end tag at or after [index] in
/// [text], outside comments and CDATA sections, or null.
///
/// [name] matches in any ASCII case, and white space may come before `>`.
({int start, int end})? _endTag(String text, String name, int index) {
  var open = text.indexOf('<', index);
  while (open >= 0) {
    final unparsedEnd = unparsedSectionEnd(text, open);
    if (unparsedEnd != null) {
      if (unparsedEnd < 0) return null;
      open = text.indexOf('<', unparsedEnd);
      continue;
    }
    if (text.startsWith('/', open + 1) &&
        _isNameAt(text, open + 2, name, true)) {
      final end = _skipWhitespace(text, open + 2 + name.length);
      if (end < text.length && text.codeUnitAt(end) == _greaterThan) {
        return (start: open, end: end + 1);
      }
    }
    open = text.indexOf('<', open + 1);
  }
  return null;
}

/// Whether the element name at [index] in [text] is [name], in any ASCII
/// case with [anyCase].
bool _isNameAt(String text, int index, String name, bool anyCase) {
  final end = index + name.length;
  if (end > text.length ||
      end < text.length && !_endsName(text.codeUnitAt(end))) {
    return false;
  }
  return anyCase
      ? _equalsIgnoringAsciiCase(text, index, name)
      : text.startsWith(name, index);
}

/// Whether [text] holds [other] at [index], ignoring the case of ASCII
/// letters only.
bool _equalsIgnoringAsciiCase(String text, int index, String other) {
  for (var offset = 0; offset < other.length; offset++) {
    final unit = text.codeUnitAt(index + offset);
    final expected = other.codeUnitAt(offset);
    if (unit == expected) continue;
    final lower = unit | 0x20;
    if (lower != (expected | 0x20) || lower < 0x61 || lower > 0x7A) {
      return false;
    }
  }
  return true;
}

int _skipWhitespace(String text, int index) {
  var cursor = index;
  while (cursor < text.length && isRegExpWhitespace(text.codeUnitAt(cursor))) {
    cursor++;
  }
  return cursor;
}

bool _endsName(int codeUnit) =>
    isRegExpWhitespace(codeUnit) ||
    codeUnit == _slash ||
    codeUnit == _lessThan ||
    codeUnit == _greaterThan;

bool _endsAttributeName(int codeUnit) =>
    _endsName(codeUnit) ||
    codeUnit == _equals ||
    codeUnit == _quote ||
    codeUnit == _apostrophe;

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
