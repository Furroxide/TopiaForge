/// How the `Include`, `Update` and `Remove` attributes of an MSBuild item
/// name packages, read as MSBuild reads them.
///
/// The XML reader replaces entity and character references first, so
/// `&#59;` splits a value as `;` does and `&#42;` is a wildcard. MSBuild then
/// splits the value at each `;`, trims each part as .NET's
/// `char.IsWhiteSpace` decides, and replaces each `%XX` escape, after which
/// an escaped `;`, `*` or `?` is a plain character. NuGet trims an included
/// id again once it is unescaped, so an escaped space around it still names
/// the package.
///
/// A literal id matches as .NET's ordinal comparison ignoring case matches
/// it, which for an ASCII id folds only ASCII letters. An `Update` or
/// `Remove` part with a `*` or `?` names the items before it that the
/// wildcard matches, ignoring case as a .NET regular expression does. An
/// `Include` part with one names files, never a package. A part that holds
/// a property, item or metadata expression names what that evaluates to,
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
      include: attribute == 'Include',
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
/// [package]. In an [include], a part with `*` or `?` names files, and the
/// part's ends are trimmed again after it is unescaped.
PackageNaming _naming(String raw, String package, {required bool include}) {
  final value = _withoutXmlReferences(raw);
  // XML rejects a malformed reference, so MSBuild cannot load the project.
  if (value == null) return PackageNaming.none;
  var names = false;
  var alone = true;
  for (final part in value.split(';')) {
    var start = 0;
    var end = part.length;
    while (start < end && _isWhiteSpace(part.codeUnitAt(start))) {
      start++;
    }
    while (end > start && _isWhiteSpace(part.codeUnitAt(end - 1))) {
      end--;
    }
    if (start == end) continue;
    final trimmed = part.substring(start, end);
    if (_expressionOpeners.any(trimmed.contains)) {
      alone = false;
      continue;
    }
    // Already trimmed with MSBuild's whitespace rules. Without escapes or
    // wildcards, the code-unit view needs neither decoding nor a second trim.
    final spec =
        !trimmed.contains('%') &&
            !trimmed.contains('*') &&
            !trimmed.contains('?')
        ? trimmed.codeUnits
        : _unescaped(trimmed, trim: include);
    final wildcard = spec.contains(_anyRun) || spec.contains(_anyOne);
    final matches = wildcard
        ? !include && _matches(spec, package)
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
/// each `*` and `?` that was not escaped as [_anyRun] or [_anyOne], and
/// with [trim], without the white space at either end, as NuGet trims an id.
List<int> _unescaped(String part, {required bool trim}) {
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
  if (!trim) return units;
  var start = 0;
  var end = units.length;
  while (start < end && _isWhiteSpace(units[start])) {
    start++;
  }
  while (end > start && _isWhiteSpace(units[end - 1])) {
    end--;
  }
  return units.sublist(start, end);
}

/// Whether .NET's `char.IsWhiteSpace` holds for [unit], as it does where
/// MSBuild trims a part and NuGet an id: tab to carriage return, space,
/// U+0085, U+00A0, and the space, line and paragraph separators. Unlike
/// `\s`, it holds for U+0085 and not for U+FEFF.
bool _isWhiteSpace(int unit) => switch (unit) {
  >= 0x09 && <= 0x0D ||
  0x20 ||
  0x85 ||
  0xA0 ||
  0x1680 ||
  >= 0x2000 && <= 0x200A ||
  0x2028 ||
  0x2029 ||
  0x202F ||
  0x205F ||
  0x3000 => true,
  _ => false,
};

/// Whether [spec], which holds no wildcard, is [package] in any ASCII case.
///
/// NuGet and MSBuild compare ids with .NET's ordinal comparison ignoring
/// case, which matches no other code unit to an ASCII letter: not U+0131
/// (dotless i), U+017F (long s), U+0130 (capital I with a dot) or U+212A
/// (the Kelvin sign).
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
            _sameInWildcard(unit, package.codeUnitAt(cursor))) {
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

/// Whether the code unit [unit] of a wildcard matches [other] of an id.
///
/// MSBuild matches a wildcard as a .NET regular expression that ignores
/// case, which also matches a letter whose lowercase is ASCII: U+0130
/// (capital I with a dot) to `i`, and U+212A (the Kelvin sign) to `k`.
bool _sameInWildcard(int unit, int other) =>
    _sameIgnoringAsciiCase(unit, other) ||
    unit == 0x0130 && (other | 0x20) == 0x69 ||
    unit == 0x212A && (other | 0x20) == 0x6B;

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
