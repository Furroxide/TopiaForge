/// How the `Include`, `Update` and `Remove` attributes of an MSBuild item
/// name packages, read as MSBuild reads them.
///
/// The XML reader replaces entity and character references first, so
/// `&#59;` splits a value as `;` does and `&#42;` is a wildcard. MSBuild then
/// splits the value at each `;`, trims the white space around each part,
/// and replaces each `%XX` escape, after which an escaped `;`, `*` or `?` is
/// a plain character. An `Update` or `Remove` part with a `*` or `?` names
/// the items before it that the wildcard matches, in any case as on
/// Windows. An `Include` part with one names files, never a package. A part
/// that holds a
/// property, item or metadata expression names what that evaluates to,
/// which a scan of the text cannot know, so it names no package here.
library;

import 'linear_text_scan.dart';

/// How the value of an item attribute names a package.
enum PackageNaming {
  /// It names no package that a scan of the text can tell is this one.
  none,

  /// It names the package and nothing else.
  alone,

  /// It names the package in a list, by a wildcard or next to an
  /// expression, so an edit to the item changes what else it names too.
  shared,
}

/// How the first attribute named exactly [attribute] of the start [tag] in
/// [text] that names [package] at all names it, or [PackageNaming.none].
///
/// [attribute] is `Include`, `Update` or `Remove`, whose case MSBuild
/// requires. [package] is a NuGet id, which matches in any ASCII case.
PackageNaming packageNaming(
  String text,
  StartTag tag,
  String attribute,
  String package,
) {
  for (final candidate in tag.attributes) {
    if (!isAttributeNamed(text, candidate, attribute)) continue;
    final naming = _naming(
      text.substring(candidate.valueStart + 1, candidate.end - 1),
      package,
      wildcards: attribute != 'Include',
    );
    if (naming != PackageNaming.none) return naming;
  }
  return PackageNaming.none;
}

/// The first `PackageReference` item in [text] whose `Include` names
/// [package], outside comments and CDATA sections, with its bounds and
/// whether it names other packages too, or null.
({int start, int end, bool shared})? firstPackageReference(
  String text,
  String package,
) {
  for (final item in packageReferences(text)) {
    final naming = packageNaming(text, item.tag, 'Include', package);
    if (naming == PackageNaming.none) continue;
    return (
      start: item.start,
      end: item.end,
      shared: naming == PackageNaming.shared,
    );
  }
  return null;
}

/// How the attribute value [raw], as written between its quotes, names
/// [package]. With [wildcards], a part with `*` or `?` matches ids.
PackageNaming _naming(String raw, String package, {required bool wildcards}) {
  final value = _withoutXmlReferences(raw);
  // XML rejects a malformed reference, so MSBuild cannot load the project.
  if (value == null) return PackageNaming.none;
  var names = false;
  var alone = true;
  for (final part in value.split(';')) {
    var start = 0;
    var end = part.length;
    while (start < end && isRegExpWhitespace(part.codeUnitAt(start))) {
      start++;
    }
    while (end > start && isRegExpWhitespace(part.codeUnitAt(end - 1))) {
      end--;
    }
    if (start == end) continue;
    final trimmed = part.substring(start, end);
    if (_expressionOpeners.any(trimmed.contains)) {
      alone = false;
      continue;
    }
    final spec = _unescaped(trimmed);
    final wildcard = spec.contains(_anyRun) || spec.contains(_anyOne);
    final matches = wildcard
        ? wildcards && _matches(spec, package)
        : _isId(spec, package);
    names = names || matches;
    alone = alone && matches && !wildcard;
  }
  if (!names) return PackageNaming.none;
  return alone ? PackageNaming.alone : PackageNaming.shared;
}

const _expressionOpeners = [r'$(', '@(', '%('];

/// A `*` that [_unescaped] read as a wildcard: any run of code units.
const _anyRun = -1;

/// A `?` that [_unescaped] read as a wildcard: any one code unit.
const _anyOne = -2;

/// The code units of [part] after MSBuild replaces each `%XX` escape, with
/// each `*` and `?` that was not escaped as [_anyRun] or [_anyOne].
List<int> _unescaped(String part) {
  final units = <int>[];
  for (var index = 0; index < part.length; index++) {
    final unit = part.codeUnitAt(index);
    final high = unit == 0x25 && index + 2 < part.length
        ? _hexDigit(part.codeUnitAt(index + 1))
        : -1;
    final low = high < 0 ? -1 : _hexDigit(part.codeUnitAt(index + 2));
    if (low >= 0) {
      units.add(high * 16 + low);
      index += 2;
    } else {
      units.add(
        unit == 0x2A
            ? _anyRun
            : unit == 0x3F
            ? _anyOne
            : unit,
      );
    }
  }
  return units;
}

/// Whether [spec], which holds no wildcard, is [package] in any ASCII case.
bool _isId(List<int> spec, String package) {
  if (spec.length != package.length) return false;
  for (var index = 0; index < spec.length; index++) {
    if (!_sameIgnoringAsciiCase(spec[index], package.codeUnitAt(index))) {
      return false;
    }
  }
  return true;
}

/// Whether the wildcard [spec] matches [package] in any ASCII case.
bool _matches(List<int> spec, String package) {
  // Each unit but a run takes one code unit, so a spec with more cannot
  // match. That bounds the rest by the id's length, however long [spec] is.
  if (spec.where((unit) => unit != _anyRun).length > package.length) {
    return false;
  }
  final pattern = <int>[
    for (var index = 0; index < spec.length; index++)
      if (spec[index] != _anyRun || index == 0 || spec[index - 1] != _anyRun)
        spec[index],
  ];
  var at = 0;
  var cursor = 0;
  var run = -1;
  var resume = 0;
  while (cursor < package.length) {
    final unit = at < pattern.length ? pattern[at] : null;
    if (unit == _anyOne ||
        unit != null &&
            unit >= 0 &&
            _sameIgnoringAsciiCase(unit, package.codeUnitAt(cursor))) {
      at++;
      cursor++;
    } else if (unit == _anyRun) {
      run = at++;
      resume = cursor;
    } else if (run >= 0) {
      at = run + 1;
      cursor = ++resume;
    } else {
      return false;
    }
  }
  while (at < pattern.length && pattern[at] == _anyRun) {
    at++;
  }
  return at == pattern.length;
}

bool _sameIgnoringAsciiCase(int unit, int other) {
  if (unit == other) return true;
  final lower = unit | 0x20;
  return lower == (other | 0x20) && lower >= 0x61 && lower <= 0x7A;
}

int _hexDigit(int unit) => switch (unit) {
  >= 0x30 && <= 0x39 => unit - 0x30,
  >= 0x41 && <= 0x46 => unit - 0x37,
  >= 0x61 && <= 0x66 => unit - 0x57,
  _ => -1,
};

/// [value] with its entity and character references replaced, as the XML
/// reader replaces them, or null when one is malformed.
String? _withoutXmlReferences(String value) {
  var ampersand = value.indexOf('&');
  if (ampersand < 0) return value;
  final replaced = StringBuffer();
  var from = 0;
  while (ampersand >= 0) {
    final semicolon = value.indexOf(';', ampersand + 1);
    if (semicolon < 0) return null;
    final name = value.substring(ampersand + 1, semicolon);
    final text = _entities[name] ?? _characterReference(name);
    if (text == null) return null;
    replaced
      ..write(value.substring(from, ampersand))
      ..write(text);
    from = semicolon + 1;
    ampersand = value.indexOf('&', from);
  }
  replaced.write(value.substring(from));
  return replaced.toString();
}

const _entities = {'lt': '<', 'gt': '>', 'amp': '&', 'quot': '"', 'apos': "'"};

/// The character that the reference `&name;` stands for, as `#` and decimal
/// digits or `#x` and hex digits, or null when it is not one.
///
/// MSBuild's reader accepts a reference to any code point, even one that
/// XML forbids, such as `&#1;`.
String? _characterReference(String name) {
  if (!name.startsWith('#')) return null;
  final hex = name.startsWith('#x');
  final digits = name.substring(hex ? 2 : 1);
  if (digits.isEmpty) return null;
  var code = 0;
  for (final unit in digits.codeUnits) {
    final digit = hex
        ? _hexDigit(unit)
        : unit >= 0x30 && unit <= 0x39
        ? unit - 0x30
        : -1;
    if (digit < 0) return null;
    code = code * (hex ? 16 : 10) + digit;
    if (code > 0x10FFFF) return null;
  }
  return String.fromCharCode(code);
}
