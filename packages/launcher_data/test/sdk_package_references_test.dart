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
      // an edit to a list would change the other package too.
      final items = [
        '<PackageReference Update="$_abstractions" Version="0.0.1" />',
        '<PackageReference Include="$_abstractions" Version="0.0.1" />',
        '<PackageReference Update=" topiaforge.mods.abstractions " '
            'version="0.0.2" PrivateAssets="all" Condition="true" />',
        '<PackageReference Update="$_abstractions">\n'
            '      <Version>0.0.3</Version>\n'
            '    </PackageReference>',
        '<PackageReference Update="$_abstractions;Other" Version="0.0.4" />',
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
