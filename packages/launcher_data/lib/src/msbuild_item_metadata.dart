/// The metadata of an MSBuild item, set by its attributes and child
/// elements.
///
/// MSBuild reads each child element of an item as metadata, and each
/// attribute other than the item's own, such as `Include` or `Condition`.
/// It matches metadata names ignoring case, applies a later attribute after
/// an earlier one, and applies the children after the attributes. So a
/// `<Version>` child overrides a `Version` attribute without any error, an
/// empty `<Version />` clears it, and a `version` attribute after `Version`
/// wins. Like the scanners in `linear_text_scan.dart`, these examine each
/// code unit a bounded number of times.
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
/// [item] runs from its start tag to its own `/>` or end tag, as the item
/// finder bounds one. [name] holds only ASCII letters, and a child matches
/// it in any case. Comments and CDATA sections are skipped. A child's start
/// tag ends at the first `>` outside its quoted values, and an element child
/// at the next end tag outside comments and CDATA sections, since metadata
/// hold only text. The search stops at a child that is left open.
Iterable<({int start, int end})> metadataElements(
  String item,
  String name,
) sync* {
  // Only the item's own end tag can follow its children. A self-closing
  // item has no end tag, and no children.
  final itemEnd = item.lastIndexOf('</');
  var open = _markup(item, item.indexOf('<', 1));
  while (open >= 0 && open < itemEnd) {
    var nameEnd = open + 1;
    while (nameEnd < itemEnd && !_endsName(item.codeUnitAt(nameEnd))) {
      nameEnd++;
    }
    if (!_isName(item, open + 1, nameEnd, name)) {
      open = _markup(item, item.indexOf('<', nameEnd));
      continue;
    }
    final tagEnd = _startTagEnd(item, nameEnd);
    if (tagEnd < 0 || tagEnd >= itemEnd) return;
    var end = tagEnd + 1;
    if (item.codeUnitAt(tagEnd - 1) != _slash) {
      var endTag = _markup(item, item.indexOf('<', end));
      while (endTag >= 0 &&
          endTag < itemEnd &&
          !item.startsWith('</', endTag)) {
        endTag = _markup(item, item.indexOf('<', endTag + 1));
      }
      final close = endTag >= 0 && endTag < itemEnd
          ? item.indexOf('>', endTag)
          : -1;
      if (close < 0 || close >= itemEnd) return;
      end = close + 1;
    }
    yield (start: open, end: end);
    open = _markup(item, item.indexOf('<', end));
  }
}

/// The first `<` in [text] at or after [open] that opens markup rather than
/// a comment or CDATA section, or -1, also when one of those does not close.
int _markup(String text, int open) {
  var cursor = open;
  while (cursor >= 0) {
    final unparsedEnd = unparsedSectionEnd(text, cursor);
    if (unparsedEnd == null) return cursor;
    if (unparsedEnd < 0) return -1;
    cursor = text.indexOf('<', unparsedEnd);
  }
  return -1;
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

/// The bounds of each attribute of the MSBuild [item]'s start tag that sets
/// the metadata [name], in order.
///
/// [item] starts with its start tag, as the item finder bounds one, and
/// [readStartTag] reads that tag. [name] holds only ASCII letters, and
/// an attribute matches it in any case. An attribute runs from its name to
/// its closing quote: white space may surround its `=`, and its value may
/// take either quote, so a `>` inside the value does not end the tag.
/// Comments and children come after the start tag, so no text in them is
/// read as an attribute. A start tag that [readStartTag] cannot read yields
/// none, such as one with an unquoted value, no white space between
/// attributes, a `<` in a value, or no end.
Iterable<({int start, int end})> metadataAttributes(String item, String name) =>
    [
      for (final attribute
          in readStartTag(item, 0)?.attributes ?? const <TagAttribute>[])
        if (_isName(item, attribute.start, attribute.nameEnd, name))
          (start: attribute.start, end: attribute.end),
    ];

/// [item] with the metadata [name] set to [value] by a single attribute.
///
/// The first attribute that [metadataAttributes] finds for [name] becomes
/// `name="value"`, and each later one goes with the white space before it.
/// Without any, ` name="value"` follows the start tag's last attribute, so
/// the attributes before it keep their order. A start tag that
/// [readStartTag] cannot read is left as it is. [value] holds no `"`.
String withMetadataAttribute(String item, String name, String value) {
  final tag = readStartTag(item, 0);
  if (tag == null) return item;
  final attribute = '$name="$value"';
  final matches = [
    for (final existing in tag.attributes)
      if (_isName(item, existing.start, existing.nameEnd, name)) existing,
  ];
  if (matches.isEmpty) {
    final end = tag.attributesEnd;
    return '${item.substring(0, end)} $attribute${item.substring(end)}';
  }
  final edited = StringBuffer(item.substring(0, matches.first.start))
    ..write(attribute);
  var from = matches.first.end;
  for (final later in matches.skip(1)) {
    edited.write(item.substring(from, later.leading));
    from = later.end;
  }
  edited.write(item.substring(from));
  return edited.toString();
}

/// [item] without the attributes that [metadataAttributes] finds for
/// [name], each with the white space before it. A start tag that
/// [readStartTag] cannot read is left as it is.
String withoutMetadataAttributes(String item, String name) {
  final tag = readStartTag(item, 0);
  if (tag == null) return item;
  final kept = StringBuffer();
  var from = 0;
  for (final attribute in tag.attributes) {
    if (!_isName(item, attribute.start, attribute.nameEnd, name)) continue;
    kept.write(item.substring(from, attribute.leading));
    from = attribute.end;
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
