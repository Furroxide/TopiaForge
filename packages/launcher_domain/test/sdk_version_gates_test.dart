import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

void main() {
  test('launcher names the mod and the loader update it needs', () {
    final manifest = testManifest(
      'example.feature',
      loaderRange: '>=0.2.0 <0.3.0',
    );
    final issues = const DependencyPlanner().runtimeCompatibilityIssues(
      manifest,
      gameVersion: '0.0.2545',
      loaderVersion: '0.1.0-rc.1',
      sdkVersion: '0.1.0-rc.1',
      platform: 'windows',
      architecture: 'x64',
    );
    expect(issues.single.subjectId, 'example.feature');
    expect(
      issues.single.message,
      'Fixture example.feature needs TopiaForge 0.2.0 or newer; update TopiaForge.',
    );
    final resolved = const DependencyPlanner().resolveInstalled(
      [
        InstalledMod(
          id: manifest.id,
          name: manifest.name,
          version: manifest.version,
          enabled: true,
          restartRequired: false,
          uninstallPending: false,
          packagePath: 'packages/example.feature',
          manifest: manifest,
        ),
      ],
      loaderVersion: '0.1.0-rc.1',
      sdkVersion: '0.1.0-rc.1',
    );
    expect(resolved.orderedMods, isEmpty);
  });

  test(
    'an older peer cannot enable the package and admission names it',
    () async {
      final manifest = testManifest(
        'example.feature',
        loaderRange: '>=0.2.0 <0.3.0',
      );
      final plan = await planJoin([
        (testPackage('example.feature'), installedOf(manifest)),
      ], environment: environmentWith(runtimeVersion: '0.1.0-rc.1'));
      expect(plan.isJoinable, isFalse);
      final blocker = plan.blockers.single;
      expect(blocker.modId, 'example.feature');
      expect(blocker.code, JoinBlockerCode.runtimeOutdated);
      expect(
        blocker.message,
        contains('needs TopiaForge 0.2.0 or newer; update TopiaForge'),
      );
    },
  );

  // The TF1010 fix raises both floors, and a player is told once what to do.
  test('both raised floors give one update message', () async {
    final manifest = testManifest(
      'example.feature',
      loaderRange: '>=0.2.0 <0.3.0',
      sdkRange: '>=0.2.0 <0.3.0',
    );
    const update =
        'Fixture example.feature needs TopiaForge 0.2.0 or newer; '
        'update TopiaForge.';
    final issues = const DependencyPlanner().runtimeCompatibilityIssues(
      manifest,
      gameVersion: '0.0.2545',
      loaderVersion: '0.1.0-rc.1',
      sdkVersion: '0.1.0-rc.1',
      platform: 'windows',
      architecture: 'x64',
    );
    expect(issues.map((issue) => issue.message), [update]);
    final plan = await planJoin([
      (testPackage('example.feature'), installedOf(manifest)),
    ], environment: environmentWith(runtimeVersion: '0.1.0-rc.1'));
    expect(plan.blockers.single.message, update);
  });

  test('an excluded floor or a ceiling keeps its exact advice', () {
    List<String> messages(String loaderRange, String sdkRange, String at) =>
        const DependencyPlanner()
            .runtimeCompatibilityIssues(
              testManifest(
                'example.feature',
                loaderRange: loaderRange,
                sdkRange: sdkRange,
              ),
              gameVersion: '0.0.2545',
              loaderVersion: at,
              sdkVersion: at,
              platform: 'windows',
              architecture: 'x64',
            )
            .map((issue) => issue.message)
            .toList();
    for (final at in ['0.1.0-rc.1', '0.2.0']) {
      expect(messages('>0.2.0', '*', at), [
        'Fixture example.feature needs a TopiaForge newer than 0.2.0; '
            'update TopiaForge.',
      ]);
    }
    expect(messages('>=0.1.0 <0.2.0', '>=0.1.0 <0.2.0', '0.3.0'), [
      'Fixture example.feature supports loader >=0.1.0 <0.2.0, not 0.3.0.',
      'Fixture example.feature supports SDK >=0.1.0 <0.2.0, not 0.3.0.',
    ]);
  });

  test('different floors give one update to the stricter requirement', () {
    for (final ranges in [
      ('>=0.2.0 <1.0.0', '>=0.3.0 <1.0.0', '0.3.0 or newer'),
      ('>=0.3.0 <1.0.0', '>=0.2.0 <1.0.0', '0.3.0 or newer'),
      ('>=0.2.0 <1.0.0', '>0.2.0 <1.0.0', 'newer than 0.2.0'),
    ]) {
      final issues = const DependencyPlanner().runtimeCompatibilityIssues(
        testManifest(
          'example.feature',
          loaderRange: ranges.$1,
          sdkRange: ranges.$2,
        ),
        loaderVersion: '0.1.0',
        sdkVersion: '0.1.0',
      );
      expect(issues, hasLength(1));
      expect(issues.single.message, contains(ranges.$3));
    }
  });

  test('feature floors use SemVer minimums, including prereleases', () {
    final policy = ManifestFeatureRequirements([
      ManifestFeatureRequirement(
        name: 'test feature',
        minimumLoaderVersion: '0.2.0',
        isUsed: (_) => true,
      ),
    ]);
    for (final range in [
      '*',
      '<1.0.0',
      '>=0.1.0 <1.0.0',
      '>=0.2.0-rc.1 <1.0.0',
    ]) {
      expect(
        policy
            .validate(testManifest('example.feature', loaderRange: range))
            .single
            .message,
        contains('TFPKG180'),
      );
    }
    for (final range in ['0.2.0', '>=0.2.0 <1.0.0', '>=0.3.0 <1.0.0']) {
      expect(
        policy.validate(testManifest('example.feature', loaderRange: range)),
        isEmpty,
      );
    }
  });
}
