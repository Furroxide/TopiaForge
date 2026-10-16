import 'package:launcher_data/src/msbuild_item_metadata.dart';
import 'package:test/test.dart';

/// How long a scan may take on input built to make it backtrack or copy.
const _budget = Duration(milliseconds: 100);

List<String> _elements(String item, String name) => [
  for (final element in metadataElements(item, name))
    item.substring(element.start, element.end),
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
}
