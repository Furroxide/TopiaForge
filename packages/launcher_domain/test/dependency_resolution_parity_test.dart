import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('installed dependency resolution parity', () {
    test('enforces known, compatible game builds for constrained mods', () {
      final constrained = _installed(
        _manifest(
          'constrained.mod',
          gameVersionRange: VersionRange.parse('0.0.2309'),
        ),
      );
      const planner = DependencyPlanner();

      final compatible = planner.resolveInstalled(
        [constrained],
        gameVersion: '0.0.2309',
        requireKnownGameVersion: true,
      );
      final incompatible = planner.resolveInstalled(
        [constrained],
        gameVersion: '0.0.2228',
        requireKnownGameVersion: true,
      );
      final unknown = planner.resolveInstalled([
        constrained,
      ], requireKnownGameVersion: true);

      expect(compatible.orderedMods, [constrained]);
      expect(incompatible.orderedMods, isEmpty);
      expect(unknown.orderedMods, isEmpty);
      expect(
        unknown.issues.map((issue) => issue.message).join(' '),
        contains('could not verify the installed build'),
      );
    });

    test('blocks both parties to a declared conflict', () {
      final alpha = _manifest(
        'alpha.mod',
        conflicts: const [ModConflict(id: 'beta.mod')],
      );
      final beta = _manifest('beta.mod');

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(alpha),
        _installed(beta),
      ]);

      expect(resolution.orderedMods, isEmpty);
      expect(
        resolution.issues
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.subjectId?.toLowerCase())
            .toSet(),
        containsAll(<String?>{'alpha.mod', 'beta.mod'}),
      );
    });

    test('orders a satisfied installed optional dependency first', () {
      final consumer = _manifest(
        'consumer.mod',
        optionalDependencies: const [ModDependency(id: 'provider.mod')],
      );
      final provider = _manifest('provider.mod');

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(consumer),
        _installed(provider),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'provider.mod',
        'consumer.mod',
      ]);
    });

    test('mutual optional dependencies remain non-blocking', () {
      final alpha = _manifest(
        'alpha.mod',
        dependencies: const [ModDependency(id: 'beta.mod', optional: true)],
      );
      final beta = _manifest(
        'beta.mod',
        optionalDependencies: const [ModDependency(id: 'alpha.mod')],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(beta),
        _installed(alpha),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'beta.mod',
        'alpha.mod',
      ]);
    });

    test('mutual loadAfter hints remain non-blocking', () {
      final alpha = _manifest('alpha.mod', loadAfter: const ['beta.mod']);
      final beta = _manifest('beta.mod', loadAfter: const ['alpha.mod']);

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(alpha),
        _installed(beta),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'beta.mod',
        'alpha.mod',
      ]);
    });

    test('loadBefore orders its owner before the named mod', () {
      final alpha = _manifest('alpha.mod', loadBefore: const ['beta.mod']);
      final beta = _manifest('beta.mod');

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(beta),
        _installed(alpha),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'alpha.mod',
        'beta.mod',
      ]);
    });

    test('a soft reverse edge cannot invalidate a hard dependency', () {
      final consumer = _manifest(
        'consumer.mod',
        dependencies: const [ModDependency(id: 'provider.mod')],
      );
      final provider = _manifest(
        'provider.mod',
        loadAfter: const ['consumer.mod'],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(provider),
        _installed(consumer),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'provider.mod',
        'consumer.mod',
      ]);
    });

    test('optional ordering takes precedence over reverse loadAfter', () {
      final consumer = _manifest(
        'consumer.mod',
        optionalDependencies: const [ModDependency(id: 'provider.mod')],
      );
      final provider = _manifest(
        'provider.mod',
        loadAfter: const ['consumer.mod'],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(provider),
        _installed(consumer),
      ]);

      expect(resolution.hasBlockingIssues, isFalse);
      expect(resolution.orderedMods.map((mod) => mod.id), [
        'provider.mod',
        'consumer.mod',
      ]);
    });

    test('excludes duplicate enabled manifest ids deterministically', () {
      final first = _installed(
        _manifest('Duplicate.Mod'),
        packagePath: '/packages/z-last',
      );
      final second = _installed(
        _manifest('duplicate.mod'),
        packagePath: '/packages/a-first',
      );
      final unrelated = _installed(_manifest('unrelated.mod'));
      final planner = const DependencyPlanner();

      final forward = planner.resolveInstalled([first, second, unrelated]);
      final reverse = planner.resolveInstalled([second, first, unrelated]);

      expect(forward.orderedMods.map((mod) => mod.id), ['unrelated.mod']);
      expect(reverse.orderedMods.map((mod) => mod.id), ['unrelated.mod']);
      final forwardDuplicate = forward.issues.singleWhere(
        (issue) => issue.message.startsWith('Multiple enabled packages'),
      );
      final reverseDuplicate = reverse.issues.singleWhere(
        (issue) => issue.message.startsWith('Multiple enabled packages'),
      );
      expect(reverseDuplicate.message, forwardDuplicate.message);
      expect(
        forwardDuplicate.message.indexOf('/packages/a-first'),
        lessThan(forwardDuplicate.message.indexOf('/packages/z-last')),
      );
    });

    test(
      'does not form a false cycle through an unsatisfied required edge',
      () {
        final alpha = _manifest(
          'alpha.mod',
          dependencies: [
            ModDependency(
              id: 'beta.mod',
              versionRange: VersionRange.parse('>=2.0.0'),
            ),
          ],
        );
        final beta = _manifest(
          'beta.mod',
          dependencies: const [ModDependency(id: 'alpha.mod')],
        );

        final resolution = const DependencyPlanner().resolveInstalled([
          _installed(alpha),
          _installed(beta),
        ]);

        expect(resolution.hasBlockingIssues, isTrue);
        expect(
          resolution.issues.map((issue) => issue.message),
          isNot(
            contains(
              predicate(
                (message) => message?.toString().contains('cycle') ?? false,
              ),
            ),
          ),
        );
      },
    );

    test('excludes runtime-incompatible mods and their dependents', () {
      // Deliberately below the shipped loader/SDK line so the mod is genuinely
      // runtime-incompatible; the exact bound moves with the product version.
      final incompatible = _manifest(
        'incompatible.mod',
        loaderVersionRange: VersionRange.parse('<0.0.9'),
        sdkVersionRange: VersionRange.parse('<0.0.9'),
      );
      final dependent = _manifest(
        'dependent.mod',
        dependencies: const [ModDependency(id: 'incompatible.mod')],
      );
      final unrelated = _manifest('unrelated.mod');

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(incompatible),
        _installed(dependent),
        _installed(unrelated),
      ]);

      expect(resolution.orderedMods.map((mod) => mod.id), ['unrelated.mod']);
      expect(
        resolution.issues.map((issue) => issue.message).join(' '),
        allOf(contains('supports loader'), contains('supports SDK')),
      );
    });

    test('excludes every cycle member and mods depending on the cycle', () {
      final alpha = _manifest(
        'alpha.mod',
        dependencies: const [ModDependency(id: 'beta.mod')],
      );
      final beta = _manifest(
        'beta.mod',
        dependencies: const [ModDependency(id: 'alpha.mod')],
      );
      final dependent = _manifest(
        'dependent.mod',
        dependencies: const [ModDependency(id: 'alpha.mod')],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(alpha),
        _installed(beta),
        _installed(dependent),
      ]);

      expect(resolution.hasBlockingIssues, isTrue);
      expect(resolution.orderedMods, isEmpty);
    });

    test('propagates a missing dependency block to dependents', () {
      final broken = _manifest(
        'broken.mod',
        dependencies: const [ModDependency(id: 'missing.mod')],
      );
      final dependent = _manifest(
        'dependent.mod',
        dependencies: const [ModDependency(id: 'broken.mod')],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(broken),
        _installed(dependent),
      ]);

      expect(resolution.hasBlockingIssues, isTrue);
      expect(resolution.orderedMods, isEmpty);
    });

    test('blocks invalid manifests without treating warnings as errors', () {
      final warningOnly = _manifest(
        'warning.mod',
        license: 'not an spdx expression!',
      );
      final invalid = _manifest(
        'invalid.mod',
        dependencies: const [ModDependency(id: '../outside')],
      );

      final resolution = const DependencyPlanner().resolveInstalled([
        _installed(warningOnly),
        _installed(invalid),
      ]);

      expect(resolution.orderedMods.map((mod) => mod.id), ['warning.mod']);
      expect(
        resolution.issues
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.subjectId),
        contains('invalid.mod'),
      );
    });
  });
}

ModManifest _manifest(
  String id, {
  String version = '1.0.0',
  List<ModDependency> dependencies = const [],
  List<ModDependency> optionalDependencies = const [],
  List<ModConflict> conflicts = const [],
  List<String> loadAfter = const [],
  List<String> loadBefore = const [],
  VersionRange gameVersionRange = const VersionRange.any(),
  VersionRange loaderVersionRange = const VersionRange.any(),
  VersionRange sdkVersionRange = const VersionRange.any(),
  String license = '',
  List<String> capabilities = const [],
}) {
  return ModManifest(
    schemaVersion: ModManifest.currentSchemaVersion,
    id: id,
    name: id,
    version: version,
    author: const ModAuthor(name: 'TopiaForge'),
    entryAssembly: '$id.dll',
    entryType: '$id.Entry',
    dependencies: dependencies,
    optionalDependencies: optionalDependencies,
    conflicts: conflicts,
    loadAfter: loadAfter,
    loadBefore: loadBefore,
    gameVersionRange: gameVersionRange,
    loaderVersionRange: loaderVersionRange,
    sdkVersionRange: sdkVersionRange,
    license: license,
    capabilities: capabilities,
  );
}

InstalledMod _installed(ModManifest manifest, {String? packagePath}) {
  return InstalledMod(
    id: manifest.id,
    name: manifest.name,
    version: manifest.version,
    enabled: true,
    restartRequired: false,
    uninstallPending: false,
    packagePath: packagePath ?? '/packages/${manifest.id}',
    manifest: manifest,
  );
}
