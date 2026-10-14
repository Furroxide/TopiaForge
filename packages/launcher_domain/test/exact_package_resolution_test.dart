import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

ExactSourcePackage _offer(
  ModManifest manifest, {
  String? sha256,
  bool enabled = true,
  String source = 'official',
  int? size,
}) => ExactSourcePackage(
  sourceId: 'io.github.furroxide.topiaforge.$source',
  sourceName: 'Source $source',
  enabled: enabled,
  manifest: manifest,
  downloadUrl: 'https://docs.topiaforge.dev/registry/${manifest.id}.zip',
  sha256: sha256 ?? testSha(manifest.id, manifest.version),
  sizeBytes: size,
);

JoinPackageResolution _resolve(
  ExactPackageCatalog catalog, {
  bool developerMode = false,
  bool hostServesPackages = false,
  String version = '1.0.0',
}) => catalog.resolvePackage(
  testPackage('example.sandbox', version: version),
  developerMode: developerMode,
  hostServesPackages: hostServesPackages,
);

void main() {
  final current = testManifest('example.sandbox');
  final older = testManifest('example.sandbox', version: '0.9.0');

  group('exact package resolution', () {
    test('an installed exact archive comes first', () {
      final result = _resolve(
        ExactPackageCatalog(
          installed: [
            ExactInstalledArchive(
              sha256: testSha('example.sandbox'),
              manifest: current,
            ),
          ],
          sources: [_offer(current)],
        ),
      );
      expect(result.availability, JoinPackageAvailability.installed);
      expect(result.manifest!.version, '1.0.0');
    });

    test('an enabled source offers the exact archive, history included', () {
      final result = _resolve(
        ExactPackageCatalog(
          sources: [
            _offer(current, enabled: false, source: 'disabled'),
            _offer(older, source: 'first'),
            _offer(current, source: 'second', size: 4096),
            _offer(current, source: 'third'),
          ],
        ),
      );
      expect(result.availability, JoinPackageAvailability.available);
      expect(result.sourceName, 'Source second');
      expect(result.sizeBytes, 4096);
      expect(result.downloadUrl, endsWith('example.sandbox.zip'));
      final history = _resolve(
        ExactPackageCatalog(sources: [_offer(current), _offer(older)]),
        version: '0.9.0',
      );
      expect(history.availability, JoinPackageAvailability.available);
      expect(history.manifest!.version, '0.9.0');
    });

    test('a disabled source only explains the block', () {
      final result = _resolve(
        ExactPackageCatalog(
          sources: [_offer(current, enabled: false, source: 'off')],
        ),
        developerMode: true,
        hostServesPackages: true,
      );
      expect(result.availability, JoinPackageAvailability.inDisabledSource);
      expect(result.sourceName, 'Source off');
      expect(result.manifest, isNull);
    });

    test('another archive at the same version is a hash mismatch', () {
      final result = _resolve(
        ExactPackageCatalog(sources: [_offer(current, sha256: 'f' * 64)]),
      );
      expect(result.availability, JoinPackageAvailability.hashMismatch);
      expect(result.manifest, isNull);
    });

    test('a known mod without the version is version unavailable', () {
      final result = _resolve(ExactPackageCatalog(sources: [_offer(older)]));
      expect(result.availability, JoinPackageAvailability.versionUnavailable);
    });

    test('a mod no enabled source knows is not in sources', () {
      expect(
        _resolve(ExactPackageCatalog()).availability,
        JoinPackageAvailability.notInSources,
      );
      expect(
        _resolve(
          ExactPackageCatalog(
            sources: [_offer(older, enabled: false, source: 'off')],
          ),
        ).availability,
        JoinPackageAvailability.notInSources,
      );
    });

    test('a host offers packages only in developer mode, after sources', () {
      final catalog = ExactPackageCatalog(sources: [_offer(older)]);
      expect(
        _resolve(catalog, developerMode: true).availability,
        JoinPackageAvailability.versionUnavailable,
      );
      expect(
        _resolve(catalog, hostServesPackages: true).availability,
        JoinPackageAvailability.versionUnavailable,
      );
      expect(
        _resolve(
          catalog,
          developerMode: true,
          hostServesPackages: true,
        ).availability,
        JoinPackageAvailability.hostOffered,
      );
      expect(
        _resolve(
          ExactPackageCatalog(sources: [_offer(current)]),
          developerMode: true,
          hostServesPackages: true,
        ).availability,
        JoinPackageAvailability.available,
      );
    });

    test('only lowercase SHA-256 values and real URLs are catalogued', () {
      expect(
        () => ExactInstalledArchive(sha256: 'A' * 64, manifest: current),
        throwsArgumentError,
      );
      expect(
        () => ExactSourcePackage(
          sourceId: 'source',
          sourceName: 'Source',
          enabled: true,
          manifest: current,
          downloadUrl: ' ',
          sha256: 'a' * 64,
        ),
        throwsArgumentError,
      );
      expect(
        () => _offer(current, size: ModRegistryFormat.maxPackageBytes + 1),
        throwsRangeError,
      );
    });
  });

  group('the join planner with a catalog', () {
    test('plans from trusted manifests found by exact SHA-256', () async {
      final descriptor = testDescriptor(
        packages: [
          testPackage('example.sandbox', version: '0.9.0'),
          testPackage('example.zombies'),
        ],
      );
      final server = await verifiedForTest(descriptor);
      final plan = const JoinPlanner().planFromCatalog(
        server: server,
        catalog: ExactPackageCatalog(
          installed: [
            ExactInstalledArchive(
              sha256: testSha('example.zombies'),
              manifest: testManifest('example.zombies'),
            ),
          ],
          sources: [_offer(current), _offer(older)],
        ),
        environment: testEnvironment,
        mayContactHost: false,
      );
      expect(plan.isJoinable, isTrue, reason: plan.blockers.join('; '));
      expect(
        packageIn(plan, 'example.sandbox').disposition,
        JoinPackageDisposition.download,
      );
      expect(
        packageIn(plan, 'example.zombies').disposition,
        JoinPackageDisposition.useInstalled,
      );
      expect(plan.installActions.single.version, '0.9.0');
    });

    test(
      'an archive no source offers blocks the join with its reason',
      () async {
        final server = await verifiedForTest(
          testDescriptor(packages: [testPackage('example.sandbox')]),
        );
        final plan = const JoinPlanner().planFromCatalog(
          server: server,
          catalog: ExactPackageCatalog(sources: [_offer(older)]),
          environment: testEnvironment,
          mayContactHost: true,
        );
        expect(plan.isJoinable, isFalse);
        expect(plan.blockers.single.code, JoinBlockerCode.contentUnavailable);
        expect(
          packageIn(plan, 'example.sandbox').resolution.availability,
          JoinPackageAvailability.versionUnavailable,
        );
      },
    );
  });

  test('the descriptor fixture resolves every package', () {
    final fixture = readMultiplayerFixture('descriptor/cases.json');
    final descriptor = descriptorFromFixture(
      asObject(asObjects(fixture['valid']).first['descriptor']),
    );
    final resolutions = ExactPackageCatalog().resolve(
      descriptor,
      developerMode: false,
    );
    expect(resolutions.keys, [
      for (final package in descriptor.packages) package.sha256,
    ]);
    expect(resolutions.values.map((item) => item.availability).toSet(), {
      JoinPackageAvailability.notInSources,
    });
  });
}
