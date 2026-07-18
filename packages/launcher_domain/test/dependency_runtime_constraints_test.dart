import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  const planner = DependencyPlanner();
  const windowsTargets = ['code', 'standalonewindows64'];

  group('dependency runtime constraints', () {
    test('accepts a matching constrained root and installed mod', () {
      final manifest = _manifest(
        'constrained.mod',
        platforms: const ['windows'],
        architectures: const ['x64'],
        contentTargets: const ['standalonewindows64'],
      );

      final preview = planner.previewInstall(
        manifest,
        const [],
        platform: 'windows',
        architecture: 'x64',
        contentTargets: windowsTargets,
      );
      final resolution = planner.resolveInstalled(
        [_installed(manifest)],
        platform: 'windows',
        architecture: 'x64',
        contentTargets: windowsTargets,
      );

      expect(preview.hasBlockingIssues, isFalse);
      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.single.id, 'constrained.mod');
    });

    test('rejects each mismatching root host constraint', () {
      final manifest = _manifest(
        'constrained.mod',
        platforms: const ['windows'],
        architectures: const ['x64'],
        contentTargets: const ['standalonewindows64'],
      );

      PackageInstallPlan preview({
        required String platform,
        required String architecture,
        required List<String> contentTargets,
      }) => planner.previewInstall(
        manifest,
        const [],
        platform: platform,
        architecture: architecture,
        contentTargets: contentTargets,
      );

      expect(
        preview(
          platform: 'macos',
          architecture: 'x64',
          contentTargets: windowsTargets,
        ).hasBlockingIssues,
        isTrue,
      );
      expect(
        preview(
          platform: 'windows',
          architecture: 'arm64',
          contentTargets: windowsTargets,
        ).hasBlockingIssues,
        isTrue,
      );
      expect(
        preview(
          platform: 'windows',
          architecture: 'x64',
          contentTargets: const ['code', 'standaloneosx'],
        ).hasBlockingIssues,
        isTrue,
      );
    });

    test('selects the highest compatible provider deterministically', () {
      final root = _manifest(
        'consumer.mod',
        dependencies: const [ModDependency(id: 'provider.mod')],
      );
      final providers = [
        _registry(
          _manifest(
            'provider.mod',
            version: '4.0.0',
            platforms: const ['macos'],
          ),
        ),
        _registry(
          _manifest(
            'provider.mod',
            version: '3.0.0',
            platforms: const ['windows'],
            architectures: const ['arm64'],
          ),
        ),
        _registry(
          _manifest(
            'provider.mod',
            version: '2.0.0',
            platforms: const ['windows'],
            architectures: const ['x64'],
            contentTargets: const ['standaloneosx'],
          ),
        ),
        _registry(
          _manifest(
            'provider.mod',
            platforms: const ['windows'],
            architectures: const ['x64'],
            contentTargets: const ['standalonewindows64'],
          ),
        ),
      ];

      String selectedVersion(List<RegistryMod> available) => planner
          .previewInstall(
            root,
            const [],
            availableMods: available,
            platform: 'windows',
            architecture: 'x64',
            contentTargets: windowsTargets,
          )
          .installActions
          .singleWhere((action) => action.modId == 'provider.mod')
          .version;

      expect(selectedVersion(providers), '1.0.0');
      expect(selectedVersion(providers.reversed.toList()), '1.0.0');
    });

    test('does not duplicate a persisted compatibility diagnostic', () {
      final manifest = _manifest(
        'constrained.mod',
        platforms: const ['windows'],
      );
      final mod = _installed(
        manifest,
        errors: const ['Stored host compatibility failure.'],
      );

      final resolution = planner.resolveInstalled(
        [mod],
        platform: 'macos',
        architecture: 'x64',
        contentTargets: const ['code', 'standaloneosx'],
      );

      expect(resolution.issues, hasLength(1));
      expect(
        resolution.issues.single.message,
        'Stored host compatibility failure.',
      );
    });
  });
}

ModManifest _manifest(
  String id, {
  String version = '1.0.0',
  List<ModDependency> dependencies = const [],
  List<String> platforms = const [],
  List<String> architectures = const [],
  List<String> contentTargets = const [],
}) => ModManifest(
  schemaVersion: 4,
  id: id,
  name: id,
  version: version,
  author: const ModAuthor(name: 'TopiaForge'),
  entryAssembly: '$id.dll',
  entryType: '$id.Entry',
  dependencies: dependencies,
  platforms: platforms,
  architectures: architectures,
  contentTargets: contentTargets,
);

InstalledMod _installed(
  ModManifest manifest, {
  List<String> errors = const [],
}) => InstalledMod(
  id: manifest.id,
  name: manifest.name,
  version: manifest.version,
  enabled: true,
  restartRequired: false,
  uninstallPending: false,
  packagePath: '/packages/${manifest.id}/${manifest.version}',
  manifest: manifest,
  errors: errors,
);

RegistryMod _registry(ModManifest manifest) => RegistryMod(
  manifest: manifest,
  downloadUrl: 'file:///${manifest.id}-${manifest.version}.topiaforgemod',
  packageSha256: List.filled(64, 'a').join(),
);
