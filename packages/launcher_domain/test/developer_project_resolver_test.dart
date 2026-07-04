import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('DeveloperProjectResolver', () {
    test('selects highest compatible stable package deterministically', () {
      final project = DeveloperProject(
        schemaVersion: 1,
        id: 'creator.mod',
        name: 'Creator Mod',
        dependencies: [
          ModDependency(id: 'api.mod', versionRange: VersionRange.parse('1.x')),
        ],
      );
      final registry = [
        _registry(_manifest('api.mod', version: '1.0.0')),
        _registry(
          _manifest(
            'api.mod',
            version: '1.1.0',
            apiAssemblies: ['ref/Api.dll'],
          ),
        ),
        _registry(_manifest('api.mod', version: '1.2.0-beta.1')),
      ];

      final resolution = const DeveloperProjectResolver().resolve(
        project,
        registry,
      );

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.lock.packages.single.version, '1.1.0');
      expect(resolution.lock.packages.single.apiAssemblies, ['ref/Api.dll']);
    });

    test('allows prerelease packages only when opted in', () {
      final project = DeveloperProject(
        schemaVersion: 1,
        id: 'creator.mod',
        name: 'Creator Mod',
        dependencies: [
          ModDependency(id: 'api.mod', versionRange: VersionRange.parse('1.x')),
        ],
      );
      final registry = [
        _registry(_manifest('api.mod', version: '1.1.0')),
        _registry(_manifest('api.mod', version: '1.2.0-beta.1')),
      ];

      final resolution = const DeveloperProjectResolver().resolve(
        project,
        registry,
        includePrerelease: true,
      );

      expect(resolution.lock.packages.single.version, '1.2.0-beta.1');
    });

    test('orders transitive dependencies before dependents', () {
      final project = DeveloperProject(
        schemaVersion: 1,
        id: 'creator.mod',
        name: 'Creator Mod',
        dependencies: [
          ModDependency(
            id: 'feature.mod',
            versionRange: VersionRange.parse('>=1.0.0'),
          ),
        ],
      );
      final registry = [
        _registry(_manifest('base.mod', version: '1.0.0')),
        _registry(
          _manifest(
            'feature.mod',
            version: '1.0.0',
            dependencies: [
              ModDependency(
                id: 'base.mod',
                versionRange: VersionRange.parse('>=1.0.0'),
              ),
            ],
          ),
        ),
      ];

      final resolution = const DeveloperProjectResolver().resolve(
        project,
        registry,
      );

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.installActions.map((action) => action.modId), [
        'base.mod',
        'feature.mod',
      ]);
      expect(resolution.lock.dependencyGraph['feature.mod'], ['base.mod']);
    });
  });
}

ModManifest _manifest(
  String id, {
  String version = '1.0.0',
  List<ModDependency> dependencies = const [],
  List<String> apiAssemblies = const [],
}) {
  return ModManifest(
    schemaVersion: 2,
    id: id,
    name: id,
    version: version,
    author: const ModAuthor(name: 'QuantumWorks'),
    entryAssembly: '$id.dll',
    entryType: '$id.Entry',
    dependencies: dependencies,
    apiAssemblies: apiAssemblies,
  );
}

RegistryMod _registry(ModManifest manifest) {
  return RegistryMod(
    manifest: manifest,
    downloadUrl: 'file:///${manifest.id}-${manifest.version}.robotopiamod',
    packageSha256: manifest.version,
    sourceId: 'test',
    sourceName: 'Test',
  );
}
