import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('repairableVersion prioritizes selected and requested damage', () {
    const mod = InstalledMod(
      id: 'selection.mod',
      name: 'Selection',
      version: '1.0.0',
      enabled: true,
      restartRequired: false,
      uninstallPending: false,
      packagePath: '/packages/selection.mod/1.0.0',
      requestedVersion: '2.0.0',
      installedVersions: [
        InstalledModVersionStatus(
          version: '1.0.0',
          packagePath: '/packages/selection.mod/1.0.0',
          errors: [],
          selected: true,
        ),
        InstalledModVersionStatus(
          version: '2.0.0',
          packagePath: '/packages/selection.mod/2.0.0',
          errors: ['tampered'],
          selected: false,
          repairable: true,
        ),
        InstalledModVersionStatus(
          version: '3.0.0',
          packagePath: '/packages/selection.mod/3.0.0',
          errors: ['incompatible'],
          selected: false,
        ),
      ],
    );

    expect(mod.repairableVersion, '2.0.0');
  });

  test('incompatible versions are not presented as repairable damage', () {
    const mod = InstalledMod(
      id: 'incompatible.mod',
      name: 'Incompatible',
      version: '1.0.0',
      enabled: true,
      restartRequired: false,
      uninstallPending: false,
      packagePath: '/packages/incompatible.mod/1.0.0',
      errors: ['Platform macos is not supported.'],
      installedVersions: [
        InstalledModVersionStatus(
          version: '1.0.0',
          packagePath: '/packages/incompatible.mod/1.0.0',
          errors: ['Platform macos is not supported.'],
          selected: true,
        ),
      ],
    );

    expect(mod.repairableVersion, isNull);
  });

  test('runtime compatibility includes host and content constraints', () {
    const manifest = ModManifest(
      schemaVersion: 5,
      id: 'platform.mod',
      name: 'Platform Mod',
      version: '1.0.0',
      entryAssembly: 'PlatformMod.dll',
      entryType: 'PlatformMod.Entry',
      platforms: ['windows'],
      architectures: ['x64'],
      contentTargets: ['standalonewindows64'],
    );

    final issues = const DependencyPlanner().runtimeCompatibilityIssues(
      manifest,
      platform: 'macos',
      architecture: 'arm64',
      contentTargets: const ['code', 'standaloneosx'],
    );

    expect(issues, hasLength(3));
    expect(issues.map((issue) => issue.message).join(' '), contains('macos'));
    expect(issues.map((issue) => issue.message).join(' '), contains('arm64'));
    expect(
      issues.map((issue) => issue.message).join(' '),
      contains('standaloneosx'),
    );
  });
}
