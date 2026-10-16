import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void registerLaunchModelInvariants(Map<String, Object?> fixture) {
  final raw = ((fixture['profile']! as Map)['packages']! as List).first as Map;
  ModManifest manifest() => ModManifest.fromJson(
    jsonDecode(jsonEncode(raw['manifest'])) as Map<String, Object?>,
  );
  ResolvedPackage package() => ResolvedPackage(
    id: raw['id'],
    version: raw['version'],
    manifest: manifest(),
  );
  EffectiveProfile profile(List<ResolvedPackage> values) =>
      EffectiveProfile(profileId: 'fixture', revision: 1, packages: values);
  final request = LaunchRequest.fromJson(fixture['request']);
  for (final id in ['', '../escape', 'unicode-é', 'newline\n', 'x' * 129]) {
    test('profile model rejects invalid token ${jsonEncode(id)}', () {
      expect(
        () =>
            EffectiveProfile(profileId: id, revision: 1, packages: [package()]),
        throwsFormatException,
      );
    });
  }
  for (final revision in [-1, 2147483648]) {
    test('profile model rejects revision $revision', () {
      expect(
        () => EffectiveProfile(
          profileId: 'fixture',
          revision: revision,
          packages: [package()],
        ),
        throwsFormatException,
      );
    });
  }
  test('selected identity must agree with manifest id and version', () {
    expect(
      () => ResolvedPackage(
        id: 'other.mod',
        version: raw['version'],
        manifest: manifest(),
      ),
      throwsFormatException,
    );
    expect(
      () => ResolvedPackage(
        id: raw['id'],
        version: '9.0.0',
        manifest: manifest(),
      ),
      throwsFormatException,
    );
  });
  test('profile and plan copy caller collections and immutable identities', () {
    final selected = [package()];
    final effective = profile(selected);
    final result = LaunchResolver.resolve(effective, request);
    final plan = result.plan!;
    final before = jsonEncode(plan.toJson());
    selected.clear();
    expect(effective.packages, hasLength(1));
    expect(plan.packages, hasLength(1));
    expect(plan.packages.first, isNot(isA<ResolvedPackage>()));
    expect(() => plan.packages.clear(), throwsUnsupportedError);
    expect(() => effective.packages.clear(), throwsUnsupportedError);
    final serialized = plan.toJson();
    (serialized['packages']! as List).clear();
    expect(jsonEncode(plan.toJson()), before);
  });
  test('package captures mutable manifest contribution collections', () {
    final source = manifest();
    final worlds = [...source.contributions!.worlds];
    final modes = [...source.contributions!.gamemodes];
    final targets = [...source.contributions!.launchTargets];
    final mutable = ModManifest(
      schemaVersion: 6,
      id: source.id,
      name: source.name,
      version: source.version,
      author: source.author,
      entryAssembly: source.entryAssembly,
      entryType: source.entryType,
      capabilities: source.capabilities,
      contributions: ModContributions(
        worlds: worlds,
        gamemodes: modes,
        launchTargets: targets,
      ),
    );
    final captured = ResolvedPackage(
      id: source.id,
      version: source.version,
      manifest: mutable,
    );
    worlds.clear();
    modes.clear();
    targets.clear();
    expect(
      LaunchResolver.resolve(profile([captured]), request).resolved,
      isTrue,
    );
  });
  test('digest literal vectors fix encoding, delimiter and order', () {
    final one = PackageIdentity(id: 'base.mod', version: '1.0.0');
    final two = PackageIdentity(id: 'mode.mod', version: '2.3.4');
    expect(packageSetDigest([]), 'cbf29ce484222325');
    expect(packageSetDigest([one]), '7f1662f4d34d2839');
    expect(packageSetDigest([one, two]), '6a4a1aa84ed69e17');
    expect(packageSetDigest([two, one]), '6a4a1aa84ed69e17');
  });
  test('loaded package identity changes invalidate a plan', () {
    final plan = LaunchResolver.resolve(profile([package()]), request).plan!;
    final changed = [PackageIdentity(id: raw['id'], version: '9.0.0')];
    expect(LaunchResolver.revalidate(plan, changed).map((b) => b.code), [
      LaunchBlockCode.planPackageSetMismatch,
    ]);
  });
  test(
    'loaded manifests resolve again despite identical package identities',
    () {
      final plan = LaunchResolver.resolve(profile([package()]), request).plan!;
      final json = manifest().toJson();
      (((json['contributions']! as Map)['launchTargets']! as List).first
              as Map)['transition'] =
          'additive-arena';
      final changed = ResolvedPackage(
        id: raw['id'],
        version: raw['version'],
        manifest: ModManifest.fromJson(json),
      );
      final loaded = profile([changed]);
      final bindings = RuntimeBindingSnapshot(
        profileId: 'fixture',
        profileRevision: 1,
        packageSetDigest: plan.digest,
        boundWorldIds: [plan.worldId],
        boundGamemodeIds: [plan.gamemodeId],
      );
      expect(LaunchResolver.revalidate(plan, [changed]), isEmpty);
      expect(
        LaunchResolver.resolveAgain(
          plan,
          loaded,
          RuntimeObservation.none,
          bindings,
        ).blocks.map((b) => b.code),
        [LaunchBlockCode.planResolutionMismatch],
      );
    },
  );
  test('runtime re-resolution requires fresh bindings', () {
    final effective = profile([package()]);
    final plan = LaunchResolver.resolve(effective, request).plan!;
    expect(
      LaunchResolver.resolveAgain(plan, effective).blocks.map((b) => b.code),
      [LaunchBlockCode.gamemodeUnbound, LaunchBlockCode.worldUnbound],
    );
  });
}
