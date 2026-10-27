import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import '../bin/topiaforge.dart' as cli;

bool _usesTestFeature(ModManifest manifest) => manifest.id == 'example.feature';

final _testFeatures = ManifestFeatureRequirements([
  ManifestFeatureRequirement(
    name: 'test-only feature',
    minimumLoaderVersion: '0.1.0-rc.1',
    isUsed: _usesTestFeature,
  ),
]);

// Tool discovery is independent of the command's package validation. The real
// pack/check implementations and the built managed validator still run.
class _Repository extends LocalDeveloperRepository {
  _Repository(String root)
    : super(dataRoot: p.join(root, 'data'), manifestFeatures: _testFeatures);

  @override
  Future<EnvironmentReport> checkEnvironment() async =>
      const EnvironmentReport(checks: []);
}

void main() {
  late Directory temp;
  late Directory project;
  late LocalDeveloperRepository repository;
  late Map<String, Object?> manifest;

  void writeManifest(String range) {
    manifest['supportedLoaderVersionRange'] = range;
    File(
      p.join(project.path, 'topiaforge.mod.json'),
    ).writeAsStringSync(jsonEncode(manifest));
  }

  setUp(() {
    temp = Directory.systemTemp.createTempSync('tf-features-');
    project = Directory(p.join(temp.path, 'mod'))..createSync();
    repository = _Repository(temp.path);
    manifest = {
      'schemaVersion': 6,
      'name': 'example.feature',
      'displayName': 'Example feature',
      'version': '1.0.0',
      'author': {'name': 'Sam'},
      'entryAssembly': 'TopiaForge.ValidTestMod.dll',
      'entryType': 'TopiaForge.ValidTestMod.ValidMod',
      'supportedGameVersionRange': '*',
      'supportedSdkVersionRange': '*',
    };
    final assembly = File(
      p.join(
        '..',
        '..',
        'tests',
        'TopiaForge.ValidTestMod',
        'bin',
        'Release',
        'netstandard2.1',
        'TopiaForge.ValidTestMod.dll',
      ),
    );
    expect(
      assembly.existsSync(),
      isTrue,
      reason: 'Build the solution in Release before CLI tests.',
    );
    assembly.copySync(p.join(project.path, 'TopiaForge.ValidTestMod.dll'));
    writeManifest('>=0.1.0-rc.0 <0.2.0');
  });
  tearDown(() => temp.deleteSync(recursive: true));

  test(
    'pack refuses a feature below its loader floor before writing',
    () async {
      final output = p.join(temp.path, 'output');
      await expectLater(
        cli.runCli([
          'pack',
          '--project',
          project.path,
          '--output',
          output,
        ], developerRepository: repository),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'message',
            allOf(
              contains('TFPKG180'),
              contains('test-only feature'),
              contains('0.1.0-rc.1 or newer'),
              contains('Raise supportedLoaderVersionRange'),
            ),
          ),
        ),
      );
      expect(Directory(output).existsSync(), isFalse);
    },
  );

  test(
    'check package refuses the same feature in a directory and archive',
    () async {
      expect(
        await cli.runCli([
          'check',
          'package',
          project.path,
        ], developerRepository: repository),
        1,
      );
      // The production table is empty. This packs the original manifest without
      // granting the injected test validator any special production switch.
      final package = await LocalDeveloperRepository(
        dataRoot: p.join(temp.path, 'plain'),
      ).packModDirectory(project.path, outputDir: p.join(temp.path, 'output'));
      expect(
        await cli.runCli([
          'check',
          'package',
          package,
        ], developerRepository: repository),
        1,
      );
    },
  );

  test('raising the floor allows pack and checking its archive', () async {
    writeManifest('>=0.2.0 <0.3.0');
    final output = p.join(temp.path, 'output');
    expect(
      await cli.runCli([
        'pack',
        '--project',
        project.path,
        '--output',
        output,
      ], developerRepository: repository),
      0,
    );
    final package = Directory(output).listSync().whereType<File>().singleWhere(
      (file) => file.path.endsWith('.topiaforgemod'),
    );
    expect(
      await cli.runCli([
        'check',
        'package',
        package.path,
      ], developerRepository: repository),
      0,
    );
  });

  test('the shipped feature table is empty', () {
    expect(ManifestFeatureRequirements.current.features, isEmpty);
  });
}
