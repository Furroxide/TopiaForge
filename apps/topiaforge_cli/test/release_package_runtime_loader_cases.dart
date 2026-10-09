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

    // Both places the archive puts the loader carry its licence texts,
    // byte-identical to the canonical repository copies.
    expect(releaseLoaderNotices, same(topiaForgeRuntimeLoaderNotices));
    expect(releaseLoaderNotices, hasLength(8));
    for (final notice in releaseLoaderNotices) {
      final source = _repositoryFile(repositoryRoot.path, notice.sourcePath);
      for (final loader in [
        releaseLoaderDirectory(payload.path),
        releaseLoaderOverlayDirectory(payload.path),
      ]) {
        expect(
          File(
            p.join(
              loader,
              topiaForgeRuntimeLoaderNoticeDirectory,
              notice.fileName,
            ),
          ).readAsBytesSync(),
          source.readAsBytesSync(),
          reason: '${notice.fileName} beside $loader',
        );
      }
    }
    // The checked-in .NET notices are what runtime repair installs from a
    // checkout, so they must be the pinned package texts.
    for (final assembly in releaseLoaderAssemblies.where(
      (entry) => entry.isPinnedPackage,
    )) {
      final checkedIn = File(
        p.join(
          repositoryRoot.path,
          'third_party',
          'dotnet',
          'runtime-loader',
          '${assembly.packageId}-ThirdPartyNotices.txt',
        ),
      );
      expect(
        sha256.convert(checkedIn.readAsBytesSync()).toString(),
        assembly.thirdPartyNoticesSha256,
        reason: checkedIn.path,
      );
    }
  });

  test('release validation requires notices beside every loader', () async {
    final payload = await _windowsLoaderPayload(repositoryRoot, temp());
    expect(
      () => validateReleaseLoaderNotices(payload.path, windowsOverlay: true),
      returnsNormally,
    );

    final overlayNotice = File(
      p.join(
        releaseLoaderOverlayDirectory(payload.path),
        topiaForgeRuntimeLoaderNoticeDirectory,
        'Quicksand-OFL.txt',
      ),
    );
    final original = overlayNotice.readAsBytesSync();
    overlayNotice.writeAsStringSync('edited', mode: FileMode.append);
    expect(
      () => validateReleaseLoaderNotices(payload.path, windowsOverlay: true),
      throwsA(_stateErrorContaining('differs')),
    );
    overlayNotice.writeAsBytesSync(original);

    final loaderLicense = File(
      p.join(
        releaseLoaderDirectory(payload.path),
        topiaForgeRuntimeLoaderNoticeDirectory,
        'LICENSE',
      ),
    );
    final license = loaderLicense.readAsBytesSync();
    loaderLicense.deleteSync();
    expect(
      () => validateReleaseLoaderNotices(payload.path, windowsOverlay: false),
      throwsA(_stateErrorContaining('must include the loader licence notice')),
    );
    loaderLicense.writeAsBytesSync(license);

    // Runtime repair installs from the extracted archive, so the canonical
    // source has to ship too, not only the copies beside the loader.
    _repositoryFile(
      payload.path,
      'tools/unity-ui-bundle/Assets/Fonts/Audiowide-OFL.txt',
    ).deleteSync();
    expect(
      () => validateReleaseLoaderNotices(payload.path, windowsOverlay: true),
      throwsA(_stateErrorContaining('must include the loader licence source')),
    );
  });

  test('runtime repair from a release payload installs the notices', () async {
    final payload = await _windowsLoaderPayload(repositoryRoot, temp());
    final game = Directory(p.join(temp().path, 'game'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    File(p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('');
    final launcher = LocalLauncherRepository(
      dataRoot: p.join(temp().path, 'launcher-data'),
      repositoryRoot: payload.path,
      knownGamePath: game.path,
    );
    addTearDown(launcher.dispose);
    var install = await launcher.selectGameDirectory(game.path);

    final report = await launcher.installOrRepairRuntime(install);

    expect(
      report.ok,
      isTrue,
      reason: report.issues.map((issue) => issue.message).join(' '),
    );
    install = await launcher.selectGameDirectory(game.path);
    expect(install.loaderStatus, ComponentState.ready);
    final plugin = p.join(
      game.path,
      'BepInEx',
      'plugins',
      'TopiaForge.ModManager',
    );
    for (final notice in releaseLoaderNotices) {
      expect(
        File(
          p.join(
            plugin,
            topiaForgeRuntimeLoaderNoticeDirectory,
            notice.fileName,
          ),
        ).readAsBytesSync(),
        _repositoryFile(
          repositoryRoot.path,
          notice.sourcePath,
        ).readAsBytesSync(),
        reason: notice.fileName,
      );
    }
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

File _repositoryFile(String root, String posixPath) =>
    File(p.joinAll([root, ...p.posix.split(posixPath)]));

Matcher _stateErrorContaining(String text) => isA<StateError>().having(
  (error) => error.toString(),
  'message',
  contains(text),
);

/// A Windows payload with the loader runtime and the canonical notice sources
/// an archive carries: `copyLoaderRuntime` writes the .NET texts, and
/// `copyCommonPayload` copies the root documents and the `tools` tree.
Future<Directory> _windowsLoaderPayload(
  Directory repositoryRoot,
  Directory temp,
) async {
  final payload = Directory(p.join(temp.path, 'release-payload'))
    ..createSync(recursive: true);
  await ReleasePackagePayloadWriter(
    repositoryRoot: repositoryRoot.path,
    platform: ReleasePackagePlatform.windows,
    configuration: 'Release',
    rebuildRuntimePayload: false,
    fileOps: const ReleaseFileOps(),
    processRunner: const ReleaseProcessRunner(),
  ).copyLoaderRuntime(payload.path);
  for (final notice in releaseLoaderNotices) {
    final target = _repositoryFile(payload.path, notice.sourcePath);
    if (!target.existsSync()) {
      target.parent.createSync(recursive: true);
      _repositoryFile(
        repositoryRoot.path,
        notice.sourcePath,
      ).copySync(target.path);
    }
  }
  return payload;
}
