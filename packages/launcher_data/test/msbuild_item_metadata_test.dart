import 'package:launcher_data/src/msbuild_item_metadata.dart';
import 'package:test/test.dart';

/// How long a scan may take on input built to make it backtrack or copy.
const _budget = Duration(milliseconds: 100);

List<String> _elements(String item, String name) => [
  for (final element in metadataElements(item, name))
    item.substring(element.start, element.end),
];

List<String> _attributes(String item, String name) => [
  for (final attribute in metadataAttributes(item, name))
    item.substring(attribute.start, attribute.end),
];

/// Start tags that XML rejects: an unquoted value, no white space between
/// attributes, no `=`, an unclosed value, and no end.
const _rejectedStartTags = [
  '<PackageReference Include="X" Version=1 />',
  '<PackageReference Include="X"Version="1" />',
  '<PackageReference Include="X" Version "1" />',
  '<PackageReference Include="X" Version="1 />',
  '<PackageReference Include="X" Version="1"',
];

void main() {
  group('metadataElements', () {
    test('finds children in any case or form, outside comments', () {
      const item =
          '<PackageReference Include="X">\n'
          '  <!-- <Version>1</Version> -->\n'
          '  <version>2</version>\n'
          '  <VersionOverride>3</VersionOverride>\n'
          '  <Version Condition="2 > 1" />\n'
          '  <IncludeAssets>all</IncludeAssets>\n'
          "  <VERSION Label='>'>4</VERSION><Version/>\n"
          '</PackageReference>';
      expect(_elements(item, 'Version'), [
        '<version>2</version>',
        '<Version Condition="2 > 1" />',
        "<VERSION Label='>'>4</VERSION>",
        '<Version/>',
      ]);
      expect(_elements(item, 'IncludeAssets'), [
        '<IncludeAssets>all</IncludeAssets>',
      ]);
    });

    test('finds none in a self-closing or broken item', () {
      for (final item in const [
        '<PackageReference Include="X" Version="1" />',
        '<PackageReference Include="X"><Version>1</PackageReference>',
        '<PackageReference Include="X"><Version a="1></Version></PackageReference>',
        '<PackageReference Include="X"><!-- <Version>1</Version></PackageReference>',
      ]) {
        expect(_elements(item, 'Version'), isEmpty, reason: item);
      }
    });
  });

  group('withoutMetadataElements', () {
    test('drops each child with the white space before it', () {
      expect(
        withoutMetadataElements(
          '<PackageReference Include="X">\n'
              '  <Version>1</Version>\n'
              '  <PrivateAssets>all</PrivateAssets><Version />\n'
              '</PackageReference>',
          'Version',
        ),
        '<PackageReference Include="X">\n'
        '  <PrivateAssets>all</PrivateAssets>\n'
        '</PackageReference>',
      );
    });

    test('finishes many children or open tags within the budget', () {
      for (final children in [
        '<Version/>' * 20000,
        '<Version>' * 20000,
        '<Version ' * 20000,
        '<Version a="' * 20000,
        '<!--' * 20000,
        '<a' * 20000,
      ]) {
        final item =
            '<PackageReference Include="X">$children</PackageReference>';
        final watch = Stopwatch()..start();
        withoutMetadataElements(item, 'Version');
        watch.stop();
        expect(watch.elapsed, lessThan(_budget));
      }
    });
  });

  group('metadataAttributes', () {
    test('finds attributes in any case, spacing or quote', () {
      const item =
          '<PackageReference\n'
          '  Include="X" version=\'1\' Label="a > b"\n'
          '  VERSION = "2" VersionOverride="3"\tVersion="4"/>';
      expect(_attributes(item, 'Version'), [
        "version='1'",
        'VERSION = "2"',
        'Version="4"',
      ]);
      expect(_attributes(item, 'Label'), ['Label="a > b"']);
    });

    test('reads only the start tag', () {
      const item =
          '<PackageReference Include="X">\n'
          '  <!-- Version="1" PrivateAssets="all" -->\n'
          '  <IncludeAssets Version="2">all</IncludeAssets>\n'
          '  <PrivateAssets>all</PrivateAssets>\n'
          '</PackageReference>';
      expect(_attributes(item, 'Version'), isEmpty);
      expect(_attributes(item, 'PrivateAssets'), isEmpty);
    });

    test('finds none in a start tag XML rejects', () {
      for (final item in _rejectedStartTags) {
        expect(_attributes(item, 'Version'), isEmpty, reason: item);
      }
    });
  });

  group('withMetadataAttribute', () {
    test('sets the first attribute and drops the later ones', () {
      expect(
        withMetadataAttribute(
          '<PackageReference Include="X" version=\'0\'\n'
              '  Label="a" VERSION = "1"\n'
              '  Version="2" />',
          'Version',
          '3',
        ),
        '<PackageReference Include="X" Version="3"\n'
        '  Label="a" />',
      );
    });

    test('adds a missing attribute after the last one', () {
      expect(
        withMetadataAttribute(
          '<PackageReference\n  Include="X"\n  />',
          'Version',
          '1',
        ),
        '<PackageReference\n  Include="X" Version="1"\n  />',
      );
      expect(
        withMetadataAttribute(
          '<PackageReference Include="X" Label="a > b">\n'
              '  <Version>0</Version>\n'
              '</PackageReference>',
          'Version',
          '1',
        ),
        '<PackageReference Include="X" Label="a > b" Version="1">\n'
        '  <Version>0</Version>\n'
        '</PackageReference>',
      );
    });

    test('leaves a start tag XML rejects', () {
      for (final item in _rejectedStartTags) {
        expect(withMetadataAttribute(item, 'Version', '2'), item);
      }
    });

    test('finishes many attributes or open values within the budget', () {
      for (final attributes in [
        ' Version=""' * 20000,
        " version=''" * 20000,
        ' a=""' * 20000,
        ' a="' * 20000,
        ' a=' * 20000,
        ' a' * 20000,
        ' ' * 200000,
        ' ${'a' * 200000}=""',
      ]) {
        final item = '<PackageReference Include="X"$attributes />';
        final watch = Stopwatch()..start();
        metadataAttributes(item, 'Version').length;
        withMetadataAttribute(item, 'Version', '1');
        watch.stop();
        expect(watch.elapsed, lessThan(_budget));
      }
    });
  });
}
