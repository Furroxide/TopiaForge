/// The child metadata elements of an MSBuild item.
///
/// MSBuild reads each child element of an item as metadata, matching its
/// name ignoring case, and applies it after the item's attributes. So a
/// `<Version>` child overrides a `Version` attribute without any error, and
/// an empty `<Version />` clears it. Like the scanners in
/// `linear_text_scan.dart`, these examine each code unit a bounded number
/// of times.
library;

import 'linear_text_scan.dart';

const _quote = 0x22;
const _apostrophe = 0x27;
const _slash = 0x2F;
const _lessThan = 0x3C;
const _greaterThan = 0x3E;

/// The bounds of each child element of the MSBuild [item] that sets the
/// metadata [name], in order.
///
/// [item] runs from its start tag to its own `/>` or end tag, as
/// [firstPackageReference] bounds one. [name] holds only ASCII letters, and
/// a child matches it in any case. Comments are skipped. A child's start tag
/// ends at the first `>` outside its quoted values, and an element child at
/// the next end tag, since metadata hold only text. The search stops at a
/// child that is left open.
Iterable<({int start, int end})> metadataElements(
  String item,
  String name,
) sync* {
  // Only the item's own end tag can follow its children. A self-closing
  // item has no end tag, and no children.
  final itemEnd = item.lastIndexOf('</');
  var open = item.indexOf('<', 1);
  while (open >= 0 && open < itemEnd) {
    if (item.startsWith('<!--', open)) {
      final close = item.indexOf('-->', open + 4);
      if (close < 0) return;
      open = item.indexOf('<', close + 3);
      continue;
    }
    var nameEnd = open + 1;
    while (nameEnd < itemEnd && !_endsName(item.codeUnitAt(nameEnd))) {
      nameEnd++;
    }
    if (!_isName(item, open + 1, nameEnd, name)) {
      open = item.indexOf('<', nameEnd);
      continue;
    }
    final tagEnd = _startTagEnd(item, nameEnd);
    if (tagEnd < 0 || tagEnd >= itemEnd) return;
    var end = tagEnd + 1;
    if (item.codeUnitAt(tagEnd - 1) != _slash) {
      final endTag = item.indexOf('</', end);
      final close = endTag < itemEnd ? item.indexOf('>', endTag) : -1;
      if (close < 0 || close >= itemEnd) return;
      end = close + 1;
    }
    yield (start: open, end: end);
    open = item.indexOf('<', end);
  }
}

/// [item] without the child elements that set the metadata [name], each
/// with the white space before it.
String withoutMetadataElements(String item, String name) {
  final kept = StringBuffer();
  var from = 0;
  for (final element in metadataElements(item, name)) {
    var start = element.start;
    // This stops at the `>` before the child at the latest, so it never
    // reaches the child before.
    while (isRegExpWhitespace(item.codeUnitAt(start - 1))) {
      start--;
    }
    kept.write(item.substring(from, start));
    from = element.end;
  }
  kept.write(item.substring(from));
  return kept.toString();
}

bool _endsName(int codeUnit) =>
    isRegExpWhitespace(codeUnit) ||
    codeUnit == _slash ||
    codeUnit == _lessThan ||
    codeUnit == _greaterThan;

/// Whether `text.substring(start, end)` is [name] in any case. [name] holds
/// only ASCII letters, which differ from their other case in one bit.
bool _isName(String text, int start, int end, String name) {
  if (end - start != name.length) return false;
  for (var index = 0; index < name.length; index++) {
    if ((text.codeUnitAt(start + index) | 0x20) !=
        (name.codeUnitAt(index) | 0x20)) {
      return false;
    }
  }
  return true;
}

/// The offset of the `>` that ends a start tag, searching from [index] and
/// skipping quoted attribute values, or -1.
int _startTagEnd(String text, int index) {
  var cursor = index;
  while (cursor < text.length) {
    final unit = text.codeUnitAt(cursor);
    if (unit == _greaterThan) return cursor;
    if (unit == _quote || unit == _apostrophe) {
      cursor = text.indexOf(unit == _quote ? '"' : "'", cursor + 1);
      if (cursor < 0) return -1;
    }
    cursor++;
  }
  return -1;
}
