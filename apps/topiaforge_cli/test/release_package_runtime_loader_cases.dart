part of 'release_package_mod_sdk_test.dart';

void _registerRuntimeLoaderPayloadTests({
  required Directory repositoryRoot,
  required Directory Function() temp,
}) {
  test('loader payload is complete, pinned, and carries notices', () async {
    expect(releaseLoaderDlls, same(topiaForgeRuntimeLoaderDlls));
    expect(releaseLoaderAssemblies, same(topiaForgeRuntimeLoaderAssemblies));
    expect(releaseLoaderDlls, hasLength(13));
    expect(
      releaseLoaderDlls,
      containsAll(const [
        'System.Collections.Immutable.dll',
        'System.Reflection.Metadata.dll',
        'TopiaForge.Mods.Chronos.dll',
        'TopiaForge.Mods.CreatorContent.dll',
        'TopiaForge.Mods.Interop.Unity.dll',
        'TopiaForge.Mods.Multiplayer.dll',
        'TopiaForge.Mods.Prompts.dll',
        'TopiaForge.Mods.RobotKit.dll',
        'TopiaForge.Mods.Worlds.dll',
      ]),
    );
    final payload = Directory(p.join(temp().path, 'loader-payload'))
      ..createSync(recursive: true);
    await ReleasePackagePayloadWriter(
      repositoryRoot: repositoryRoot.path,
      platform: ReleasePackagePlatform.windows,
      configuration: 'Release',
      rebuildRuntimePayload: false,
      fileOps: const ReleaseFileOps(),
      processRunner: const ReleaseProcessRunner(),
    ).copyLoaderRuntime(payload.path);
    for (final dependency in releaseLoaderDlls) {
      expect(
        File(
          p.join(
            payload.path,
            'src',
            'TopiaForge.ModManager',
            'bin',
            'Release',
            'netstandard2.1',
            dependency,
          ),
        ).existsSync(),
        isTrue,
        reason: dependency,
      );
      expect(
        File(
          p.join(
            payload.path,
            'BepInEx',
            'plugins',
            'TopiaForge.ModManager',
            dependency,
          ),
        ).existsSync(),
        isTrue,
        reason: 'Windows overlay $dependency',
      );
    }
    for (final notice in runtimeLoaderNoticeNames) {
      expect(
        File(
          p.join(
            payload.path,
            'third_party',
            'dotnet',
            'runtime-loader',
            notice,
          ),
        ).existsSync(),
        isTrue,
        reason: notice,
      );
    }
    final provenance =
        jsonDecode(
              File(
                p.join(
                  payload.path,
                  'third_party',
                  'dotnet',
                  'runtime-loader',
                  'PROVENANCE.json',
                ),
              ).readAsStringSync(),
            )
            as Map<String, dynamic>;
    expect(provenance['schemaVersion'], 1);
    expect(provenance['packages'], hasLength(2));
    expect(provenance['playerProfileDependencies'], hasLength(3));
  });

  test(
    'Windows validator rejects a divergent executed loader overlay',
    () async {
      final payload = Directory(p.join(temp().path, 'overlay-integrity'))
        ..createSync(recursive: true);
      await ReleasePackagePayloadWriter(
        repositoryRoot: repositoryRoot.path,
        platform: ReleasePackagePlatform.windows,
        configuration: 'Release',
        rebuildRuntimePayload: false,
        fileOps: const ReleaseFileOps(),
        processRunner: const ReleaseProcessRunner(),
      ).copyLoaderRuntime(payload.path);
      expect(() => validateWindowsLoaderOverlay(payload.path), returnsNormally);

      final executedCopy = File(
        p.join(
          payload.path,
          'BepInEx',
          'plugins',
          'TopiaForge.ModManager',
          'TopiaForge.ModManager.dll',
        ),
      );
      executedCopy.writeAsStringSync(
        'divergent overlay',
        mode: FileMode.append,
      );

      expect(
        () => validateWindowsLoaderOverlay(payload.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.toString(),
            'message',
            allOf(contains('overlay'), contains('differs')),
          ),
        ),
      );
    },
  );
}
