import 'package:launcher_data/src/linear_text_scan.dart';
import 'package:launcher_data/src/msbuild_item_spec.dart';
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
// each comment and CDATA section taken whole. A comment ends at its first
// `-->`, so a lazy `<!--.*?-->` will not do: it stretches to a later `-->`
// when that lets the rest match.
const _attribute = r'''\s+[^\s=/<>"']+\s*=\s*(?:"[^"<]*"|'[^'<]*')''';
const _content =
    r'(?:<!--(?:(?!-->).)*-->|<!\[CDATA\[(?:(?!\]\]>).)*\]\]>'
    r'|(?!<!--|<!\[CDATA\[).)';

/// What [firstPackageReference] finds for [package] alone in an `Include`,
/// written as a pattern whose group 1 is the item.
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

/// The bounds of the item that [firstPackageReference] finds for [package]
/// in [text], which must include it alone, or null.
({int start, int end})? _found(String text, String package) {
  final found = firstPackageReference(text, package);
  if (found == null) return null;
  expect(found.shared, isFalse, reason: text);
  return (start: found.start, end: found.end);
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

    test('skips CDATA sections, whose text is not markup', () {
      const text =
          '<![CDATA[<Import Project="a" /> <!-- ]]>'
          '<Import Project="b" /><![CDATA[ <Import Project="c" />';
      expect(
        [for (final found in startTags(text, 'Import')) found.start],
        [text.indexOf('<Import Project="b"')],
      );
    });

    test('resumes after readable tags but recovers inside broken ones', () {
      const first = '<Import A="a > b" />';
      const broken = '<Import A="broken';
      const last = "<Import B='ok'/>";
      const text = 'x$first$broken$last';
      final tags = startTags(text, 'Import').toList();
      expect(
        [for (final found in tags) found.start],
        [1, 1 + first.length + broken.length],
      );
      expect(
        [for (final found in tags) _attributes(text, found.tag)],
        [
          ['A="a > b"'],
          ["B='ok'"],
        ],
      );
      final tag = tags.first.tag;
      expect((tag.nameEnd, tag.attributesEnd, tag.end), (8, 18, 21));
      expect(tag.attributes, [
        (leading: 8, start: 9, nameEnd: 10, valueStart: 11, end: 18),
      ]);
      expect(tags.last.tag.end, text.length);
      expect(
        startTags(text, 'Import', start: 1 + first.length).single.start,
        tags.last.start,
      );
    });
  });

  group('packageReferences', () {
    test('bounds each item from a start, skipping unclosed ones', () {
      const closed = '<PackageReference Update="A" />';
      const outer = '<PackageReference Include="B">';
      const inner = '<packagereference Remove="C"/>';
      const end = '</PackageReference >';
      const open = '<PackageReference Include="D">';
      const text = 'x$closed$outer$inner$end<!-- $closed -->$closed$open';
      final items = [
        for (final item in packageReferences(text, start: 1))
          text.substring(item.start, item.end),
      ];
      expect(items, [closed, '$outer$inner$end', inner, closed]);
      expect(packageReferences(text, start: text.indexOf(inner)), hasLength(2));
    });

    test('filters end candidates without changing recovery or sections', () {
      const open = '<PackageReference Update="A">';
      const close = '</pAcKaGeReFeReNcE\u00A0>';
      for (final body in const [
        '<Other Label="a > b"/><Other>',
        '<Other Label="',
        '<Other Label="<!-- </PackageReference> -->',
        '<<Other><',
        '<!OTHER <Other/>',
        '<?instruction?><Other/>',
        '<Other/></PackageReferences></PackageReference/>'
            '</PackageReference x></PackageReference\u0085>',
        '<Other/></Pac\u212AageReference>',
        '<Other/><!-- </PackageReference> <![CDATA[ -->'
            '<![CDATA[</PackageReference> <!-- ]]>',
        '<Other/><!-- --><!-- </PackageReference> -->'
            '<![CDATA[]]><![CDATA[</PackageReference>]]>',
      ]) {
        final text = 'x$open$body$close';
        final item = packageReferences(text, start: 1).single;
        expect(
          (item.start, item.end, item.tag.end),
          (1, text.length, 1 + open.length),
          reason: body,
        );
      }
      for (final section in const ['<!--', '<![CDATA[']) {
        expect(packageReferences('$open<Other/>$section$close'), isEmpty);
      }
      for (final body in const ['<', '<Other/><']) {
        expect(packageReferences('$open$body'), isEmpty);
      }
    });

    test('keeps nested starts and independent lazy traversals', () {
      const open = '<PackageReference>';
      const inner = '<packagereference/>';
      const close = '</PackageReference>';
      const tail = '<PackageReference Update="tail"/>';
      const text = '$open$inner$close$tail<!--';
      final items = packageReferences(text);
      final first = items.iterator;
      final second = items.iterator;
      expect(first.moveNext(), isTrue);
      expect(first.current.end, open.length + inner.length + close.length);
      expect(second.moveNext(), isTrue);
      expect(
        (second.current.start, second.current.end),
        (first.current.start, first.current.end),
      );
      expect(first.moveNext(), isTrue);
      expect(
        (first.current.start, first.current.end),
        (open.length, open.length + inner.length),
      );
      expect(second.current.start, 0);
      expect(items.last.end, text.length - '<!--'.length);
      expect(packageReferences('$open$inner'), hasLength(1));
    });

    test('finishes nested or unclosed items within the budget', () {
      const open = '<PackageReference Update="A">';
      const end = '</PackageReference>';
      for (final (text, count) in [
        ('${open * 8000}${end * 8000}', 8000),
        (open * 8000, 0),
        ('$open${'<!-- -->' * 20000}$end' * 2, 2),
      ]) {
        expect(withinBudget(() => packageReferences(text).length), count);
      }
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
        expect(_found('  $item\n<Other />', _abstractions), (
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
        '<![CDATA[<PackageReference Include="$_abstractions" />]]>',
        '<PackageReferences Include="$_abstractions" />',
        '<PackageReference Include="$_abstractions" Label="<" />',
      ]) {
        expect(_found(text, _abstractions), isNull, reason: text);
      }
      expect(
        _found(
          '<PackageReference Include="$_abstractions" />',
          'TopiaForge.Mods',
        ),
        isNull,
      );
    });

    test('ends each item at its own `/>` or end tag', () {
      // The first scanner ran a self-closing item on to the next
      // `</PackageReference>`, and the one after it ended a start tag at a
      // `>` inside a value, with the same effect. An end tag in a comment or
      // a CDATA section does not end the item.
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
        '<PackageReference Include="$_abstractions">\n'
            '  <Description><![CDATA[</PackageReference>]]></Description>\n'
            '</PackageReference>',
      ]) {
        expect(_found('$item$later', _abstractions), (
          start: 0,
          end: item.length,
        ), reason: item);
      }
    });

    test('agrees with the pattern on generated input', () {
      // The two plain item starts are listed twice, so that enough samples
      // hold an item next to the comments and CDATA sections.
      final samples = generatedSamples(const [
        '<PackageReference Include="$_abstractions"',
        '<PackageReference Include="TopiaForge.Mods"',
        '<PackageReference Include="TopiaForge.Mods"',
        '<PackageReference Include="$_abstractions"',
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
        '<![CDATA[</PackageReference>]]>',
        '<![CDATA[',
        ']]>',
        'x',
      ], seed: 4);
      for (final package in const [_abstractions, 'TopiaForge.Mods']) {
        final pattern = _packageReferencePattern(package);
        expectAgreement(
          samples,
          (text) => _found(text, package),
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
