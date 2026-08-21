part of 'release_package_builder_test.dart';

void _registerReleaseIdentityTests() {
  test(
    'binds the embedded ecosystem to its normalized tree and assets',
    () async {
      final repo = _writeFixtureRepo(temp);
      final launcher = Directory(p.join(temp.path, 'canonical-launcher'))
        ..createSync(recursive: true);
      _writeFile(launcher, ['topiaforge_launcher'], 'launcher');
      _writeFile(launcher, [
        'data',
        'flutter_assets',
        'NOTICES.Z',
      ], 'Flutter notices');
      final cli = File(p.join(temp.path, 'canonical-cli'))
        ..writeAsStringSync('cli');
      final dist = Directory(p.join(repo.path, 'dist'));
      final canonicalSha = ReleaseEcosystemIdentity.digestDirectory(dist);
      final zipPath = await ReleasePackageBuilder(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.linux,
        outputRoot: p.join(temp.path, 'canonical-out'),
        prebuiltLauncher: launcher.path,
        prebuiltCli: cli.path,
        rebuildRuntimePayload: false,
      ).build();

      await ReleasePackageValidator(
        platform: ReleasePackagePlatform.linux,
        zipPath: zipPath,
        requireRuntimePayload: false,
        expectedCanonicalEcosystemSha256: canonicalSha,
        canonicalAssetsDirectory: dist.path,
      ).validate();

      File(
        p.join(dist.path, 'demo.topiaforgemod'),
      ).writeAsStringSync('tampered');
      await expectLater(
        () => ReleasePackageValidator(
          platform: ReleasePackagePlatform.linux,
          zipPath: zipPath,
          requireRuntimePayload: false,
          expectedCanonicalEcosystemSha256: canonicalSha,
          canonicalAssetsDirectory: dist.path,
        ).validate(),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('Standalone mod assets'),
          ),
        ),
      );
    },
  );
}
