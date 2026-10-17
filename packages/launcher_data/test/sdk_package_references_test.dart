import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

const _abstractions = 'TopiaForge.Mods.Abstractions';
const _analyzers = 'TopiaForge.Mods.Analyzers';
const _robotKit = 'TopiaForge.Mods.RobotKit';

/// How long an edit may take on input built to make a scan backtrack.
const _budget = Duration(milliseconds: 100);

String _project(String items) =>
    '<Project Sdk="Microsoft.NET.Sdk">\n$items\n</Project>\n';

void main() {
  group('withPinnedSdkPackageReferences', () {
    test('pins each item and adds the missing ones in one ItemGroup', () {
      // `.` in an id matches only itself, so the lookalike stays as it is.
      final pinned = withPinnedSdkPackageReferences(
        _project(
          '  <ItemGroup>\n'
          '    <PackageReference Include="$_abstractions" Version="0.0.1" />\n'
          '    <PackageReference Include="TopiaForge-Mods-RobotKit" Version="2.0.0" />\n'
          '  </ItemGroup>',
        ),
        packages: const [_robotKit, _analyzers, _abstractions],
        version: '1.0.0',
      );

      expect(
        pinned,
        _project(
          '  <ItemGroup>\n'
          '    <PackageReference Include="$_abstractions" Version="1.0.0" />\n'
          '    <PackageReference Include="TopiaForge-Mods-RobotKit" Version="2.0.0" />\n'
          '  </ItemGroup>\n'
          '  <ItemGroup>\n'
          '    <PackageReference Include="$_analyzers" Version="1.0.0" PrivateAssets="all" />\n'
          '    <PackageReference Include="$_robotKit" Version="1.0.0" />\n'
          '  </ItemGroup>',
        ),
      );
    });

    test('keeps later Update and Remove items from undoing the pin', () {
      // MSBuild applies an Update or Remove item to the items before it,
      // even under a true condition. The earlier Update does not apply, and
      // the list sets no Version.
      final items = [
        '<PackageReference Update="$_abstractions" Version="0.0.1" />',
        '<PackageReference Include="$_abstractions" Version="0.0.1" />',
        '<PackageReference Update=" topiaforge.mods.abstractions " '
            'version="0.0.2" PrivateAssets="all" Condition="true" />',
        '<PackageReference Update="$_abstractions">\n'
            '      <Version>0.0.3</Version>\n'
            '    </PackageReference>',
        '<PackageReference Update="$_abstractions;Other" PrivateAssets="all" />',
        '<PackageReference Update="Other" Version="2.0.0" />',
        '<PackageReference Remove="$_abstractions" Condition="true" />',
      ];
      String project(List<String> items, String later) => _project(
        '  <ItemGroup>\n${[for (final item in items) '    $item'].join('\n')}\n'
        '  </ItemGroup>\n  <ItemGroup>$later\n  </ItemGroup>',
      );
      String pin(String text) => withPinnedSdkPackageReferences(
        text,
        packages: const [_abstractions],
        version: '1.0.0',
      );

      final pinned = pin(
        project(items, '\n    <PackageReference Remove="$_abstractions" />'),
      );

      expect(
        pinned,
        project([
          items[0],
          '<PackageReference Include="$_abstractions" Version="1.0.0" />',
          '<PackageReference Update=" topiaforge.mods.abstractions " '
              'PrivateAssets="all" Condition="true" />',
          '<PackageReference Update="$_abstractions">\n'
              '    </PackageReference>',
          items[4],
          items[5],
        ], ''),
      );
      expect(pin(pinned), pinned);
    });

    test('reads ids as MSBuild does, with references and escapes', () {
      // The XML reader replaces `&#115;`, and MSBuild unescapes `%73`, so
      // both name Abstractions. An escaped `*` is a plain character, and an
      // expression could name anything, so those items stay as they are.
      String project(String items) =>
          _project('  <ItemGroup>\n$items\n  </ItemGroup>');
      final pinned = withPinnedSdkPackageReferences(
        project(
          '    <PackageReference Include="TopiaForge.Mods.Abstraction&#115;" Version="0.0.1" />\n'
          '    <PackageReference Update="TopiaForge.Mods.Abstraction%73" Version="0.0.2" PrivateAssets="all" />\n'
          '    <PackageReference Update="TopiaForge.Mods.%2A" Version="0.0.3" />\n'
          '    <PackageReference Update="\$(Extra)" Version="0.0.4" />\n'
          '    <PackageReference Remove=" topiaforge.mods.abstractions; " />',
        ),
        packages: const [_abstractions],
        version: '1.0.0',
      );

      expect(
        pinned,
        project(
          '    <PackageReference Include="TopiaForge.Mods.Abstraction&#115;" Version="1.0.0" />\n'
          '    <PackageReference Update="TopiaForge.Mods.Abstraction%73" PrivateAssets="all" />\n'
          '    <PackageReference Update="TopiaForge.Mods.%2A" Version="0.0.3" />\n'
          '    <PackageReference Update="\$(Extra)" Version="0.0.4" />',
        ),
      );
    });

    test('refuses an item that names the package with others', () {
      // An edit to such an item would change the other packages too. NuGet
      // keeps the first item that includes a package and drops later
      // copies, and MSBuild applies a later Update or Remove to the pin.
      String project(String items) =>
          _project('  <ItemGroup>\n$items\n  </ItemGroup>');
      String pin(String items) => withPinnedSdkPackageReferences(
        project(items),
        packages: const [_abstractions],
        version: '1.0.0',
      );
      const pinned =
          '    <PackageReference Include="$_abstractions" Version="1.0.0" />';
      for (final (items, written) in const [
        (
          '    <PackageReference Include="Other;$_abstractions" Version="0.0.1" />\n'
              '$pinned',
          'Include="Other;$_abstractions"',
        ),
        (
          '$pinned\n'
              '    <PackageReference Update="$_abstractions;Other" Version="0.0.1" />',
          'Update="$_abstractions;Other"',
        ),
        (
          '$pinned\n'
              '    <PackageReference Update="TopiaForge.Mods.*">\n'
              '      <Version>0.0.1</Version>\n'
              '    </PackageReference>',
          'Update="TopiaForge.Mods.*"',
        ),
        (
          '$pinned\n'
              '    <PackageReference Update="$_abstractions&#59;Other" version="0.0.1" />',
          'Update="$_abstractions&#59;Other"',
        ),
        (
          '$pinned\n'
              '    <PackageReference Remove="Other;$_abstractions" />',
          'Remove="Other;$_abstractions"',
        ),
        (
          '$pinned\n'
              '    <PackageReference Remove="TopiaForge.Mods.Abstraction?" Condition="true" />',
          'Remove="TopiaForge.Mods.Abstraction?"',
        ),
      ]) {
        expect(
          () => pin(items),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              allOf(startsWith('$_abstractions cannot be'), contains(written)),
            ),
          ),
          reason: items,
        );
      }
      // MSBuild does not apply the earlier Update, and the later one sets
      // no Version, so the pin holds with both in place.
      for (final items in const [
        '    <PackageReference Update="$_abstractions;Other" Version="0.0.1" />\n'
            '$pinned',
        '$pinned\n'
            '    <PackageReference Update="TopiaForge.Mods.*" PrivateAssets="all" />',
      ]) {
        expect(pin(items), project(items));
      }
    });

    test('skips items and end tags inside CDATA sections', () {
      // A CDATA section holds text. The pin used to edit the description
      // instead of the item, and to end the item at the end tag in its
      // description, which kept the Version child after it.
      const description =
          '  <PropertyGroup>\n'
          '    <Description><![CDATA[<PackageReference Include="$_abstractions" Version="0.0.9" />]]></Description>\n'
          '  </PropertyGroup>\n';
      String pin(String text) => withPinnedSdkPackageReferences(
        text,
        packages: const [_abstractions],
        version: '1.0.0',
      );
      final pinned = pin(
        _project(
          '$description'
          '  <ItemGroup>\n'
          '    <PackageReference Include="$_abstractions">\n'
          '      <Description><![CDATA[</PackageReference>]]></Description>\n'
          '      <Version>0.0.1</Version>\n'
          '    </PackageReference>\n'
          '  </ItemGroup>',
        ),
      );

      expect(
        pinned,
        _project(
          '$description'
          '  <ItemGroup>\n'
          '    <PackageReference Include="$_abstractions" Version="1.0.0">\n'
          '      <Description><![CDATA[</PackageReference>]]></Description>\n'
          '    </PackageReference>\n'
          '  </ItemGroup>',
        ),
      );
      expect(pin(pinned), pinned);
    });

    test('refuses to add an item to a project with no end tag', () {
      expect(
        () => withPinnedSdkPackageReferences(
          '<Project Sdk="Microsoft.NET.Sdk">',
          packages: const [_robotKit],
          version: '1.0.0',
        ),
        throwsStateError,
      );
    });
  });

  group('withoutPackageReference', () {
    test('removes only the first item for the exact id', () {
      const lookalike =
          '<PackageReference Include="TopiaForge-Mods-RobotKit" />';
      const item = '<PackageReference Include="$_robotKit" Version="1.0.0" />';
      const later =
          '<PackageReference Include="Newtonsoft.Json" Version="13.0.3">\n'
          '</PackageReference>';
      final project = _project('$lookalike\n$item\n$later\n$item');

      expect(
        withoutPackageReference(project, _robotKit),
        _project('$lookalike\n\n$later\n$item'),
      );
      expect(withoutPackageReference(project, _abstractions), project);
    });

    test('refuses to remove an item it shares with other packages', () {
      expect(
        () => withoutPackageReference(
          _project('<PackageReference Include="$_robotKit;Other" />'),
          _robotKit,
        ),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            startsWith('$_robotKit cannot be removed'),
          ),
        ),
      );
    });

    test('removes an item however it is written, not a comment', () {
      const commented = '<!-- <PackageReference Include="$_robotKit" /> -->';
      for (final item in const [
        '<PackageReference Version="1.0.0" Include = " topiaforge.mods.robotkit " />',
        '<packagereference Include="$_robotKit" Label="a > b">\n'
            '</PACKAGEREFERENCE >',
      ]) {
        final project = _project('$commented\n$item\n$commented');
        expect(
          withoutPackageReference(project, _robotKit),
          _project('$commented\n\n$commented'),
          reason: item,
        );
      }
    });
  });

  test('both edits finish malformed projects within the budget', () {
    // An analyzer, so both metadata scans run over each item found.
    const open = '<PackageReference Include="$_analyzers"';
    for (final items in [
      '$open>' * 20000,
      '${open * 20000}>',
      '$open>${'<Version>' * 20000}</PackageReference>',
      '$open>${'<PrivateAssets a="' * 20000}</PackageReference>',
      '$open>${'<!--' * 20000}</PackageReference>',
      '$open${' a="1"' * 20000} />',
      '$open${' a="1"' * 20000} b="/>',
      '$open>${'<![CDATA[' * 20000}</PackageReference>',
      '$open />${'<PackageReference Update="TopiaForge.*" />' * 20000}',
      '<PackageReference Include="${'Other;' * 20000}" />',
    ]) {
      final project = _project(items);
      for (final edit in [
        () => withPinnedSdkPackageReferences(
          project,
          packages: const [_analyzers],
          version: '1.0.0',
        ),
        () => withoutPackageReference(project, _analyzers),
      ]) {
        final watch = Stopwatch()..start();
        edit();
        watch.stop();
        expect(watch.elapsed, lessThan(_budget));
      }
    }
  });
}
