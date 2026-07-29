part of 'launcher_data_test.dart';

void _registerRuntimeLoaderPayloadTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() gameRoot,
}) {
  const moduleAndInteropDlls = <String>[
    'TopiaForge.Mods.Chronos.dll',
    'TopiaForge.Mods.CreatorContent.dll',
    'TopiaForge.Mods.Interop.Unity.dll',
    'TopiaForge.Mods.Multiplayer.dll',
    'TopiaForge.Mods.Prompts.dll',
    'TopiaForge.Mods.RobotKit.dll',
    'TopiaForge.Mods.Ugc.dll',
    'TopiaForge.Mods.Worlds.dll',
  ];
  const validatorDependencyDlls = <String>[
    'System.Collections.Immutable.dll',
    'System.Reflection.Metadata.dll',
  ];

  File installedDll(String name) => File(
    p.join(
      gameRoot().path,
      'BepInEx',
      'plugins',
      'TopiaForge.ModManager',
      name,
    ),
  );

  test('runtime repair deploys the complete V1 managed payload', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);

    final report = await repository().installOrRepairRuntime(install);

    expect(report.ok, isTrue);
    expect(topiaForgeRuntimeLoaderAssemblies, hasLength(14));
    expect(topiaForgeRuntimeLoaderDlls, hasLength(14));
    expect(topiaForgeRuntimeLoaderDlls, containsAll(moduleAndInteropDlls));
    final packages = {
      for (final assembly in topiaForgeRuntimeLoaderAssemblies.where(
        (entry) => entry.isPinnedPackage,
      ))
        assembly.packageId: assembly,
    };
    expect(packages.keys, {
      'System.Collections.Immutable',
      'System.Reflection.Metadata',
    });
    for (final assembly in packages.values) {
      expect(assembly.packageVersion, '10.0.9');
      expect(assembly.assemblyVersion, '10.0.0.0');
      expect(assembly.sha256, matches(RegExp(r'^[0-9a-f]{64}$')));
      expect(
        assembly.thirdPartyNoticesSha256,
        matches(RegExp(r'^[0-9a-f]{64}$')),
      );
      expect(assembly.repositoryCommit, matches(RegExp(r'^[0-9a-f]{40}$')));
    }
    expect(topiaForgeRuntimeProfileAssemblies.map((entry) => entry.fileName), {
      'System.Buffers.dll',
      'System.Memory.dll',
      'System.Runtime.CompilerServices.Unsafe.dll',
    });
    for (final dll in topiaForgeRuntimeLoaderDlls) {
      expect(
        installedDll(dll).existsSync(),
        isTrue,
        reason: 'runtime repair must deploy $dll',
      );
    }
  });

  test(
    'every missing module or interop contract makes the loader stale',
    () async {
      var install = await repository().selectGameDirectory(gameRoot().path);
      var report = await repository().installOrRepairRuntime(install);
      expect(report.ok, isTrue);

      for (final dll in moduleAndInteropDlls) {
        installedDll(dll).deleteSync();

        install = await repository().selectGameDirectory(gameRoot().path);
        expect(install.loaderStatus, ComponentState.partial, reason: dll);
        expect(install.needsRepair, isTrue, reason: dll);

        report = await repository().installOrRepairRuntime(install);
        expect(report.ok, isTrue, reason: dll);
        expect(installedDll(dll).existsSync(), isTrue, reason: dll);
      }
    },
  );

  test('missing validator dependencies make the loader stale', () async {
    var install = await repository().selectGameDirectory(gameRoot().path);
    var report = await repository().installOrRepairRuntime(install);
    expect(report.ok, isTrue);

    for (final dll in validatorDependencyDlls) {
      installedDll(dll).deleteSync();

      install = await repository().selectGameDirectory(gameRoot().path);
      expect(install.loaderStatus, ComponentState.partial, reason: dll);
      expect(install.needsRepair, isTrue, reason: dll);

      report = await repository().installOrRepairRuntime(install);
      expect(report.ok, isTrue, reason: dll);
      expect(installedDll(dll).existsSync(), isTrue, reason: dll);
    }
  });
}
