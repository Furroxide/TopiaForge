import 'package:launcher_data/src/linear_text_scan.dart';
import 'package:test/test.dart';

import 'scan_agreement.dart';

const _abstractions = 'TopiaForge.Mods.Abstractions';

/// [literal] as a pattern that matches its ASCII letters in either case.
String _anyCase(String literal) => [
  for (final char in literal.split(''))
    char.toUpperCase() != char.toLowerCase()
        ? '[${char.toUpperCase()}${char.toLowerCase()}]'
        : RegExp.escape(char),
].join();

// An attribute as readStartTag reads one, and text outside markup with
// each comment taken whole. A comment ends at its first `-->`, so a lazy
// `<!--.*?-->` will not do: it stretches to a later `-->` when that lets
// the rest match.
const _attribute = r'''\s+[^\s=/<>"']+\s*=\s*(?:"[^"<]*"|'[^'<]*')''';
const _content = '(?:<!--(?:(?!-->).)*-->|(?!<!--).)';

/// What [firstPackageReference] finds for [package], written as a pattern
/// whose group 1 is the item.
RegExp _packageReferencePattern(String package) {
  final element = _anyCase('PackageReference');
  final id = '\\s*${_anyCase(package)}\\s*';
  return RegExp(
    '^$_content*?(<$element(?:$_attribute)*?\\s+Include\\s*=\\s*'
    '(?:"$id"|\'$id\')(?:$_attribute)*\\s*'
    '(?:/>|>$_content*?</$element\\s*>))',
    dotAll: true,
  );
}

/// The bounds of group 1 of [match], which runs to the end of the match.
({int start, int end})? _itemBounds(RegExpMatch? match) {
  final item = match?.group(1);
  return item == null
      ? null
      : (start: match!.end - item.length, end: match.end);
}

List<String> _attributes(String text, StartTag tag) => [
  for (final attribute in tag.attributes)
    text.substring(attribute.start, attribute.end),
];

void main() {
  group('readStartTag', () {
    test('reads attributes in any order, spacing or quote', () {
      const text =
          'x <PackageReference\n'
          '  Version = "1" Label=\'a > b\'\tInclude=" X "/>';
      final tag = readStartTag(text, 2)!;
      expect(_attributes(text, tag), [
        'Version = "1"',
        "Label='a > b'",
        'Include=" X "',
      ]);
      expect(tag.nameEnd, text.indexOf('\n'));
      expect(tag.attributesEnd, text.length - 2);
      expect(tag.end, text.length);
      final include = tag.attributes.last;
      expect(isAttributeNamed(text, include, 'Include'), isTrue);
      expect(isAttributeNamed(text, include, 'include'), isFalse);
      final value = trimmedAttributeValue(text, include);
      expect(text.substring(value.start, value.end), 'X');
      expect(text.substring(include.leading, include.start), '\t');
    });

    test('reads no tag that XML rejects', () {
      for (final tag in const [
        '<PackageReference Include="X" Version=1 />',
        '<PackageReference Include="X"Version="1" />',
        '<PackageReference Include="X" Version "1" />',
        '<PackageReference Include="X" Version="1 />',
        '<PackageReference Include="X" Version="<1" />',
        '<PackageReference Include="X" / >',
        '<PackageReference Include="X"',
      ]) {
        expect(readStartTag(tag, 0), isNull, reason: tag);
      }
    });
  });

  group('startTags', () {
    test('finds readable tags with the name, outside comments', () {
      const text =
          '<Import Project="a" />'
          '<!-- <Import Project="b" /> -->'
          '<ImportGroup>'
          '<import Project="c" />'
          '<Import Project="d"'
          "<Import Project='e'>";
      List<int> starts(String name, {bool anyCase = false}) => [
        for (final found in startTags(text, name, anyCase: anyCase))
          found.start,
      ];
      expect(starts('Import'), [0, text.indexOf("<Import Project='e'>")]);
      expect(starts('Import', anyCase: true), [
        0,
        text.indexOf('<import'),
        text.indexOf("<Import Project='e'>"),
      ]);
    });

    test('stops at a comment that does not close', () {
      const text = '<Import Project="a" /><!-- <Import Project="b" />';
      expect(startTags(text, 'Import'), hasLength(1));
    });
  });

  group('firstPackageReference', () {
    test('finds an item however its start tag is written', () {
      for (final item in const [
        '<PackageReference Include="$_abstractions" />',
        "<PackageReference\n  Include='$_abstractions'>\n"
            '  <PrivateAssets>all</PrivateAssets>\n'
            '</PackageReference>',
        '<PackageReference Version="1" Include="$_abstractions" />',
        "<PackageReference Condition=\"'\$(A)' != ''\"\n"
            '  Include = " topiaforge.mods.abstractions " />',
        '<PackageReference Include="$_abstractions" Label="a > b"/>',
        '<packagereference Include="$_abstractions">\n</PACKAGEREFERENCE >',
      ]) {
        expect(firstPackageReference('  $item\n<Other />', _abstractions), (
          start: 2,
          end: item.length + 2,
        ), reason: item);
      }
    });

    test('skips what MSBuild does not read as that item', () {
      for (final text in const [
        '<!-- <PackageReference Include="$_abstractions" /> -->',
        '<PackageReference include="$_abstractions" />',
        '<PackageReference Update="$_abstractions" Version="1" />',
        '<PackageReference Include="TopiaForge-Mods-Abstractions" />',
        '<PackageReference Include="$_abstractions;Other" />',
        '<PackageReferences Include="$_abstractions" />',
        '<PackageReference Include="$_abstractions" Label="<" />',
      ]) {
        expect(firstPackageReference(text, _abstractions), isNull);
      }
      expect(
        firstPackageReference(
          '<PackageReference Include="$_abstractions" />',
          'TopiaForge.Mods',
        ),
        isNull,
      );
    });

    test('ends each item at its own `/>` or end tag', () {
      // The first scanner ran a self-closing item on to the next
      // `</PackageReference>`, and the one after it ended a start tag at a
      // `>` inside a value, with the same effect. An end tag in a comment
      // does not end the item.
      const later =
          '\n<PackageReference Include="Other" Version="1">\n'
          '</PackageReference>';
      for (final item in const [
        '<PackageReference Include="$_abstractions" />',
        '<PackageReference Include="$_abstractions" Condition="1 > 0" />',
        '<PackageReference Include="$_abstractions">\n</PackageReference>',
        '<PackageReference Include="$_abstractions">\n'
            '  <!-- </PackageReference> -->\n'
            '</PackageReference>',
      ]) {
        expect(firstPackageReference('$item$later', _abstractions), (
          start: 0,
          end: item.length,
        ), reason: item);
      }
    });

    test('agrees with the pattern on generated input', () {
      final samples = generatedSamples(const [
        '<PackageReference Include="$_abstractions"',
        '<PackageReference Include="TopiaForge.Mods"',
        "<packagereference Version='1' Include = ' topiaforge.mods.abstractions '",
        "<PACKAGEREFERENCE Label='a > b' Include='topiaforge.mods'",
        '<PackageReference',
        ' Include="$_abstractions"',
        ' Include="TopiaForge.Mods"',
        " Label='a > b'",
        ' Label="<"',
        ' ',
        '\n',
        '"',
        '=',
        '/>',
        '/>',
        '>',
        '</PackageReference>',
        '</packagereference >',
        '<!-- </PackageReference> -->',
        '<!--',
        '-->',
        'x',
      ], seed: 4);
      for (final package in const [_abstractions, 'TopiaForge.Mods']) {
        final pattern = _packageReferencePattern(package);
        expectAgreement(
          samples,
          (text) => firstPackageReference(text, package),
          (text) => _itemBounds(pattern.firstMatch(text)),
        );
      }
    });

    test('finishes unclosed items, tags and comments within the budget', () {
      const open = '<PackageReference Include="$_abstractions"';
      for (final text in [
        '$open>' * 8000,
        '${open * 8000}>',
        '$open>${'<!-- -->' * 20000}',
        '$open${' a="1"' * 20000}',
        '${'<PackageReference a="' * 8000}>',
        '${'<!--' * 20000}$open />',
        '${'$open>' * 8000}${'</PackageReferenc>' * 8000}',
      ]) {
        expect(
          withinBudget(() => firstPackageReference(text, _abstractions)),
          isNull,
        );
      }
    });
  });
}
