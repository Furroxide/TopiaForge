part of 'launcher_data_test.dart';

void _registerRuntimeLoaderNoticeTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  File installedNotice(TopiaForgeRuntimeLoaderNotice notice) => File(
    p.join(
      gameRoot().path,
      'BepInEx',
      'plugins',
      'TopiaForge.ModManager',
      topiaForgeRuntimeLoaderNoticeDirectory,
      notice.fileName,
    ),
  );
  File noticeSource(TopiaForgeRuntimeLoaderNotice notice) => File(
    p.joinAll([repositoryRoot().path, ...p.posix.split(notice.sourcePath)]),
  );
  TopiaForgeRuntimeLoaderNotice named(String fileName) =>
      topiaForgeRuntimeLoaderNotices.singleWhere(
        (notice) => notice.fileName == fileName,
      );
  Future<GameInstall> repaired() async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final report = await repository().installOrRepairRuntime(install);
    expect(
      report.ok,
      isTrue,
      reason: report.issues.map((issue) => issue.message).join(' '),
    );
    return repository().selectGameDirectory(gameRoot().path);
  }

  test('loader notices name the licence of every installed assembly', () {
    expect(topiaForgeRuntimeLoaderNoticeDirectory, 'licenses');
    expect(
      {
        for (final notice in topiaForgeRuntimeLoaderNotices)
          notice.fileName: notice.sourcePath,
      },
      {
        'LICENSE': 'LICENSE',
        'THIRD_PARTY_NOTICES.md': 'THIRD_PARTY_NOTICES.md',
        'Audiowide-OFL.txt':
            'tools/unity-ui-bundle/Assets/Fonts/Audiowide-OFL.txt',
        'Quicksand-OFL.txt':
            'tools/unity-ui-bundle/Assets/Fonts/Quicksand-OFL.txt',
        'System.Collections.Immutable-LICENSE.txt':
            'third_party/dotnet/runtime-loader/LICENSE.txt',
        'System.Collections.Immutable-ThirdPartyNotices.txt':
            'third_party/dotnet/runtime-loader/'
            'System.Collections.Immutable-ThirdPartyNotices.txt',
        'System.Reflection.Metadata-LICENSE.txt':
            'third_party/dotnet/runtime-loader/LICENSE.txt',
        'System.Reflection.Metadata-ThirdPartyNotices.txt':
            'third_party/dotnet/runtime-loader/'
            'System.Reflection.Metadata-ThirdPartyNotices.txt',
      },
    );
  });

  test('a source checkout carries every loader notice source', () {
    // Runtime repair from a checkout installs these exact files.
    final checkout = p.normalize(p.join(Directory.current.path, '..', '..'));
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      expect(
        File(
          p.joinAll([checkout, ...p.posix.split(notice.sourcePath)]),
        ).existsSync(),
        isTrue,
        reason: notice.sourcePath,
      );
    }
  });

  test('runtime repair installs the notices beside the loader', () async {
    final install = await repaired();

    expect(install.loaderStatus, ComponentState.ready);
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      expect(
        installedNotice(notice).readAsStringSync(),
        noticeSource(notice).readAsStringSync(),
        reason: notice.fileName,
      );
    }
  });

  test('a loader installed without its notices is repaired', () async {
    await repaired();
    // The layout an older launcher left: every assembly, no licence folder.
    installedNotice(named('LICENSE')).parent.deleteSync(recursive: true);

    var install = await repository().selectGameDirectory(gameRoot().path);
    expect(install.loaderStatus, ComponentState.partial);
    expect(install.needsRepair, isTrue);

    install = await repaired();
    expect(install.loaderStatus, ComponentState.ready);
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      expect(
        installedNotice(notice).existsSync(),
        isTrue,
        reason: notice.fileName,
      );
    }
  });

  test('an edited notice is restored to the payload text', () async {
    await repaired();
    final notice = named('Quicksand-OFL.txt');
    installedNotice(notice).writeAsStringSync('edited');

    final install = await repository().selectGameDirectory(gameRoot().path);
    expect(install.loaderStatus, ComponentState.partial);

    await repaired();
    expect(
      installedNotice(notice).readAsStringSync(),
      noticeSource(notice).readAsStringSync(),
    );
  });

  test('runtime repair refuses to install a loader without notices', () async {
    final notice = named('Audiowide-OFL.txt');
    noticeSource(notice).deleteSync();
    final install = await repository().selectGameDirectory(gameRoot().path);

    final report = await repository().installOrRepairRuntime(install);

    expect(report.ok, isFalse);
    expect(
      report.issues.map((issue) => issue.message).join(' '),
      contains(notice.sourcePath),
    );
    // Nothing was committed: no runtime, no loader, no licence folder.
    expect(Directory(p.join(gameRoot().path, 'BepInEx')).existsSync(), isFalse);
    expect(File(p.join(gameRoot().path, 'winhttp.dll')).existsSync(), isFalse);
  });

  test(
    'a payload without notices leaves detection to the assemblies',
    () async {
      await repaired();
      installedNotice(named('LICENSE')).parent.deleteSync(recursive: true);
      for (final notice in topiaForgeRuntimeLoaderNotices) {
        final source = noticeSource(notice);
        if (source.existsSync()) {
          source.deleteSync();
        }
      }

      // It has nothing to install, so it must not demand a repair that cannot
      // succeed and would then block every launch.
      final install = await repository().selectGameDirectory(gameRoot().path);
      expect(install.loaderStatus, ComponentState.ready);
    },
  );
}
