import 'package:launcher_data/src/linear_text_scan.dart';
import 'package:test/test.dart';

import 'scan_agreement.dart';

// The patterns the scanners replaced. Each scanner must return exactly what
// its pattern matched, and must stay fast where the pattern backtracked.
RegExp _elementPattern(String name) =>
    RegExp('<$name>\\s*([^<]+?)\\s*</$name>');

RegExp _startTagPattern(String name) => RegExp('<$name(?:\\s[^>]*)?>');

RegExp _textElementPattern(String name) =>
    RegExp('<$name(?:\\s[^>]*)?>[^<]*</$name>');

final _vdfPattern = RegExp(r'"((?:\\.|[^"\\])*)"\s*"((?:\\.|[^"\\])*)"');

void main() {
  test('whitespace matches RegExp \\s for every UTF-16 code unit', () {
    final pattern = RegExp(r'^\s$');
    final disagreements = [
      for (var unit = 0; unit <= 0xFFFF; unit++)
        if (isRegExpWhitespace(unit) !=
            pattern.hasMatch(String.fromCharCode(unit)))
          unit,
    ];

    expect(disagreements, isEmpty);
  });

  group('firstElementText', () {
    test('reads the trimmed text of the first element', () {
      expect(
        firstElementText(
          '<TargetFramework>\n    net8.0 \t\r\n</TargetFramework>',
          'TargetFramework',
        ),
        'net8.0',
      );
      expect(
        firstElementText(
          r'<RobotopiaManagedDir> D:\Games\My Game\Managed </RobotopiaManagedDir>',
          'RobotopiaManagedDir',
        ),
        r'D:\Games\My Game\Managed',
      );
      expect(
        firstElementText(
          '<Version>1.0.0</Version><Version>2.0</Version>',
          'Version',
        ),
        '1.0.0',
      );
    });

    test('skips the elements the pattern skipped', () {
      expect(
        firstElementText(
          '<Version></Version><Version>2.0</Version>',
          'Version',
        ),
        '2.0',
      );
      expect(
        firstElementText(
          r'<Version>$(Base)<!-- local --></Version><Version>3.0</Version>',
          'Version',
        ),
        '3.0',
      );
      expect(
        firstElementText('<Version> 1.0 <Version>4.0</Version>', 'Version'),
        '4.0',
      );
      expect(
        firstElementText(
          '<TargetFrameworks>net8.0</TargetFrameworks>',
          'TargetFramework',
        ),
        isNull,
      );
      expect(
        firstElementText('<PackageVersion>1.0</PackageVersion>', 'Version'),
        isNull,
      );
      expect(firstElementText('<Version>1.0</version>', 'Version'), isNull);
    });

    test('trims exactly what the pattern trimmed', () {
      // `\s` covers U+00A0 and U+3000 but not U+0085, unlike String.trim.
      expect(
        firstElementText('<Version>\u00A01.0\u3000</Version>', 'Version'),
        '1.0',
      );
      expect(
        firstElementText('<Version>1.0\u0085</Version>', 'Version'),
        '1.0\u0085',
      );
      // Whitespace-only text yields its last character, as the lazy group
      // did, and still wins over a later element.
      expect(
        firstElementText(
          '<Version> \t</Version><Version>2.0</Version>',
          'Version',
        ),
        '\t',
      );
    });

    test('agrees with the pattern on generated input', () {
      for (final (name, other) in const [
        ('Version', 'PackageVersion'),
        ('PackageVersion', 'Version'),
      ]) {
        final pattern = _elementPattern(name);
        expectAgreement(
          generatedSamples([
            '<$name>',
            '<$name>',
            '</$name>',
            '</$name>',
            '<$other>',
            '</$other>',
            '<',
            '>',
            '/',
            ' ',
            ' ',
            '\t',
            '\n',
            '\u00A0',
            '\u0085',
            '1.0',
            '1.0',
            'v',
          ], seed: name.length),
          (text) => firstElementText(text, name),
          (text) => pattern.firstMatch(text)?.group(1),
        );
      }
    });

    test('finishes an unclosed element within the budget', () {
      for (final name in const [
        'RobotopiaManagedDir',
        'TargetFramework',
        'PackageVersion',
        'Version',
      ]) {
        final text = '<$name>${' ' * 1000}';
        expect(withinBudget(() => firstElementText(text, name)), isNull);
      }
    });
  });

  group('firstStartTag', () {
    const project =
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <PropertyGroup Condition="true">\n'
        '  </PropertyGroup>\n'
        '</Project>\n';

    test('finds the first start tag with or without attributes', () {
      expect(firstStartTag(project, 'Project'), (
        start: 0,
        end: project.indexOf('\n'),
      ));
      final group = project.indexOf('<PropertyGroup');
      expect(firstStartTag(project, 'PropertyGroup'), (
        start: group,
        end: project.indexOf('\n', group),
      ));
      expect(firstStartTag('<Project>', 'Project'), (start: 0, end: 9));
      expect(firstStartTag('<Project\n  Sdk="a">', 'Project'), (
        start: 0,
        end: 19,
      ));
    });

    test('skips longer names and tags the pattern rejected', () {
      expect(firstStartTag('<ProjectReference /><Project>', 'Project'), (
        start: 20,
        end: 29,
      ));
      expect(firstStartTag('<Project/>', 'Project'), isNull);
      expect(firstStartTag('<Project', 'Project'), isNull);
      // Like the pattern, attributes end at the first `>`, even in a value.
      expect(firstStartTag('<Project A="1>2">', 'Project'), (
        start: 0,
        end: 14,
      ));
    });

    test('agrees with the pattern on generated input', () {
      final samples = generatedSamples(const [
        '<Project',
        '<Project>',
        '<ProjectReference',
        '<PropertyGroup',
        '>',
        '<',
        '/',
        ' ',
        '\n',
        '\u2028',
        'a="b"',
        'x',
      ], seed: 2);
      for (final name in const ['Project', 'PropertyGroup']) {
        final pattern = _startTagPattern(name);
        expectAgreement(
          samples,
          (text) => firstStartTag(text, name),
          (text) => matchBounds(pattern.firstMatch(text)),
        );
      }
    });

    test('finishes many unterminated tags within the budget', () {
      for (final name in const [
        'Project',
        'PropertyGroup',
        'RestorePackagesWithLockFile',
        'RestoreLockedMode',
        'TopiaForgeSafeProject',
      ]) {
        final text = '<$name ' * 4000;
        expect(withinBudget(() => firstStartTag(text, name)), isNull);
      }
    });
  });

  group('firstTextElement', () {
    const name = 'TopiaForgeSafeProject';

    test('finds the first element whose text holds no markup', () {
      const plain = '<TopiaForgeSafeProject>false</TopiaForgeSafeProject>';
      expect(firstTextElement(' $plain', name), (
        start: 1,
        end: plain.length + 1,
      ));
      const attributed =
          "<TopiaForgeSafeProject Condition=\"'a' == 'a'\">true"
          '</TopiaForgeSafeProject>';
      expect(firstTextElement(attributed, name), (
        start: 0,
        end: attributed.length,
      ));
      const nested =
          '<TopiaForgeSafeProject>\$(A)<!-- x --></TopiaForgeSafeProject>';
      expect(firstTextElement('$nested$plain', name), (
        start: nested.length,
        end: nested.length + plain.length,
      ));
      expect(firstTextElement('<TopiaForgeSafeProject>true', name), isNull);
    });

    test('agrees with the pattern on generated input', () {
      final pattern = _textElementPattern(name);
      expectAgreement(
        generatedSamples(const [
          '<TopiaForgeSafeProject',
          '<TopiaForgeSafeProject>',
          '</TopiaForgeSafeProject>',
          '>',
          '<',
          ' ',
          '\n',
          'true',
          'a="b"',
        ], seed: 3),
        (text) => firstTextElement(text, name),
        (text) => matchBounds(pattern.firstMatch(text)),
      );
    });

    test('finishes many unterminated tags within the budget', () {
      final text = '${'<$name ' * 4000}>';
      expect(withinBudget(() => firstTextElement(text, name)), isNull);
    });
  });

  group('vdfQuotedPairs', () {
    test('reads key/value pairs and keeps their escapes', () {
      const library =
          '"libraryfolders"\n{\n\t"0"\n\t{\n'
          '\t\t"path"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n'
          '\t\t"label"\t\t"Say \\"hi\\""\n'
          '\t}\n}\n';
      expect(vdfQuotedPairs(library).toList(), [
        ('path', r'C:\\Program Files (x86)\\Steam'),
        ('label', r'Say \"hi\"'),
      ]);
      expect(vdfQuotedPairs('"a" "b" "c"\u00A0"d"').toList(), [
        ('a', 'b'),
        ('c', 'd'),
      ]);
      // A backslash before a line break escapes nothing and ends the string.
      expect(vdfQuotedPairs('"a\\\nb" "c"').toList(), isEmpty);
    });

    test('agrees with the pattern on generated input', () {
      expectAgreement(
        generatedSamples(const [
          '"',
          '"',
          '"k"',
          ' "v"',
          '\\',
          '\\"',
          '\n',
          '\r',
          '\u2028',
          ' ',
          '\t',
          '\u00A0',
          'a',
          '{',
        ], seed: 5),
        (text) {
          final pairs = vdfQuotedPairs(text).toList();
          return pairs.isEmpty ? null : pairs;
        },
        (text) {
          final pairs = [
            for (final match in _vdfPattern.allMatches(text))
              (match.group(1)!, match.group(2)!),
          ];
          return pairs.isEmpty ? null : pairs;
        },
      );
    });

    test('finishes unclosed strings within the budget', () {
      final escapedQuotes = r'\"' * 20000;
      for (final text in ['"$escapedQuotes', '"key" "$escapedQuotes']) {
        expect(withinBudget(() => vdfQuotedPairs(text).toList()), isEmpty);
      }
    });
  });
}
