part of 'topiaforge.dart';

/// `topiaforge world ...` — the custom-world authoring loop: pair a Unity world project with the mod that
/// ships its bundle (`link`), build the world prefab into that mod's AssetBundles/ headlessly (`build`),
/// and run the whole build → pack → install → launch chain (`play`).
extension _WorldCommands on _TopiaForgeCli {
  Future<int> _world(List<String> args) async {
    return switch (args.firstOrNull) {
      'link' => _worldLink(args.skip(1).toList()),
      'build' => _worldBuild(args.skip(1).toList()),
      'play' => _worldPlay(args.skip(1).toList()),
      _ => throw UsageError(
        'Usage: topiaforge world link|build|play ...\n'
        '  topiaforge world link --project <unityProj> --mod <modDir> [--bundle name] [--prefab assetPath]\n'
        '  topiaforge world build [--project <unityProj|name>] [--mod <modDir>] [--bundle name] [--unity Unity.exe] [--dry-run]\n'
        '  topiaforge world play [--project <unityProj|name>] [--mod <modDir>] [--bundle name] [--unity Unity.exe] [--configuration cfg]',
      ),
    };
  }

  Future<int> _worldLink(List<String> args) async {
    final projectArg = _option(args, '--project');
    final modArg = _option(args, '--mod');
    if (projectArg == null || modArg == null) {
      throw UsageError(
        'Usage: topiaforge world link --project <unityProj> --mod <modDir> [--bundle name] [--prefab assetPath]',
      );
    }
    final project = p.normalize(p.absolute(projectArg));
    if (!Directory(p.join(project, 'Assets')).existsSync() ||
        !Directory(p.join(project, 'ProjectSettings')).existsSync()) {
      throw StateError(
        '$project is not a Unity project (expected Assets/ and ProjectSettings/).',
      );
    }
    final mod = p.normalize(p.absolute(modArg));
    final manifestFile = File(p.join(mod, 'topiaforge.mod.json'));
    if (!manifestFile.existsSync()) {
      throw StateError(
        '$mod is not a mod directory (no topiaforge.mod.json). Pass --mod '
        '<dir> pointing at a mod folder, or create one with '
        '`topiaforge new mod --template world`.',
      );
    }
    final manifestJson = readBoundedJsonObjectSync(
      manifestFile,
      maxBytes: CliFileLimits.manifest,
    );
    final manifest = ModManifest.fromJson(manifestJson);
    final blockingManifestIssues = manifest
        .validate()
        .where((issue) => issue.isBlocking)
        .toList();
    if (blockingManifestIssues.isNotEmpty) {
      throw StateError(
        'topiaforge.mod.json is invalid: '
        '${blockingManifestIssues.map((issue) => issue.message).join(' ')}',
      );
    }
    final modId = manifest.id;

    final config = await developerRepository.writeWorldAuthoringConfig(
      project,
      WorldAuthoringConfig(
        worldId: modId,
        bundleName:
            _option(args, '--bundle') ??
            WorldAuthoringConfig.deriveBundleName(modId),
        worldPrefab:
            _option(args, '--prefab') ??
            WorldAuthoringConfig.defaultWorldPrefab,
        modPath: p.relative(mod, from: project),
      ),
    );
    stdout.writeln(
      'Paired $project with $mod (bundle "${config.bundleName}", prefab ${config.worldPrefab}).',
    );
    stdout.writeln(
      'Next: author the world prefab, then `topiaforge world build --project "$project"`.',
    );
    return 0;
  }

  Future<int> _worldBuild(List<String> args) async {
    final project = await _resolveUnityDevProject(_option(args, '--project'));
    if (project == null) {
      stderr.writeln(
        'No Unity world project found. Pass --project <path|name>, run from a '
        'Unity project directory, or create one with `topiaforge new unity-world`.',
      );
      return 1;
    }

    if (args.contains('--dry-run')) {
      return _worldBuildDryRun(project, args);
    }

    final result = await developerRepository.buildWorldBundle(
      unityProjectPath: project,
      modPath: _option(args, '--mod') ?? '',
      bundleName: _option(args, '--bundle') ?? '',
      unityExePath: _option(args, '--unity') ?? '',
    );
    if (!result.success) {
      stderr.writeln('World bundle build failed: ${result.errorMessage}');
      for (final line in result.logTail) {
        stderr.writeln('  | $line');
      }
      if (result.logPath.isNotEmpty) {
        stderr.writeln('Full log: ${result.logPath}');
      }
      return 1;
    }
    stdout.writeln(
      'Built ${result.bundlePath} (${result.sizeBytes} bytes, sha256=${result.sha256}) '
      'with Unity ${result.editorVersion}.',
    );
    return 0;
  }

  /// Prints the resolved project/mod/bundle/editor without launching Unity (CI-testable).
  Future<int> _worldBuildDryRun(String project, List<String> args) async {
    final config = await developerRepository.readWorldAuthoringConfig(project);
    final modArg = _option(args, '--mod') ?? '';
    final modRaw = modArg.isNotEmpty ? modArg : (config?.modPath ?? '');
    final mod = modRaw.isEmpty
        ? ''
        : p.normalize(p.isAbsolute(modRaw) ? modRaw : p.join(project, modRaw));
    final bundle = _option(args, '--bundle') ?? (config?.bundleName ?? '');
    final editors = await developerRepository.listUnityEditors();
    final eligible = editors
        .where((editor) => WorldBundleEditorGate.isEligible(editor.version))
        .toList();

    stdout.writeln('Unity project: $project');
    stdout.writeln(
      'Paired mod:    ${mod.isEmpty ? '(none — run world link)' : mod}',
    );
    stdout.writeln('Bundle name:   ${bundle.isEmpty ? '(none)' : bundle}');
    stdout.writeln(
      'World prefab:  ${config?.worldPrefab ?? WorldAuthoringConfig.defaultWorldPrefab}',
    );
    stdout.writeln(
      'Build editor:  ${eligible.isEmpty ? '(none eligible — need Unity ${RobotopiaGameUnityCompatibility.requiredEditorVersion})' : '${eligible.first.version} at ${eligible.first.path}'}',
    );
    return mod.isEmpty || bundle.isEmpty ? 1 : 0;
  }

  Future<int> _worldPlay(List<String> args) async {
    if (!await _ensureBuildTooling()) {
      return 1;
    }
    final project = await _resolveUnityDevProject(_option(args, '--project'));
    if (project == null) {
      stderr.writeln(
        'No Unity world project found. Pass --project <path|name> or run from one.',
      );
      return 1;
    }

    final build = await developerRepository.buildWorldBundle(
      unityProjectPath: project,
      modPath: _option(args, '--mod') ?? '',
      bundleName: _option(args, '--bundle') ?? '',
      unityExePath: _option(args, '--unity') ?? '',
    );
    if (!build.success) {
      stderr.writeln('World bundle build failed: ${build.errorMessage}');
      for (final line in build.logTail) {
        stderr.writeln('  | $line');
      }
      return 1;
    }
    stdout.writeln('Built ${build.bundlePath}.');

    // The bundle lands inside the mod; pack that mod and install the package.
    final config = await developerRepository.readWorldAuthoringConfig(project);
    final modRaw = _option(args, '--mod') ?? (config?.modPath ?? '');
    final mod = p.normalize(
      p.isAbsolute(modRaw) ? modRaw : p.join(project, modRaw),
    );
    final configuration = _option(args, '--configuration') ?? 'Release';
    final hasProjectFile = File(
      p.join(mod, 'topiaforge.project.json'),
    ).existsSync();
    final packagePath = hasProjectFile
        ? await developerRepository.packProject(
            mod,
            configuration: configuration,
          )
        : await developerRepository.packModDirectory(
            mod,
            configuration: configuration,
          );
    stdout.writeln('Packed $packagePath.');

    final launcher = LocalLauncherRepository();
    final install = await launcher.detectKnownInstall();
    if (install == null) {
      throw StateError(_noInstallRemedy);
    }
    await launcher.installPackage(packagePath, install);
    stdout.writeln('Installed $packagePath.');
    return _launch(const <String>[], restart: false);
  }

  Future<String?> _resolveUnityDevProject(String? selector) async {
    bool isUnityProject(String path) =>
        Directory(p.join(path, 'ProjectSettings')).existsSync() &&
        Directory(p.join(path, 'Assets')).existsSync();

    if (selector != null) {
      if (Directory(selector).existsSync()) {
        return p.normalize(p.absolute(selector));
      }
      final projects = await developerRepository.listProjects();
      for (final project in projects) {
        if (project.name.toLowerCase() == selector.toLowerCase() &&
            project.isUnity) {
          return project.path;
        }
      }
      return null;
    }

    if (isUnityProject(Directory.current.path)) {
      return Directory.current.path;
    }

    final projects = await developerRepository.listProjects();
    final worlds =
        projects
            .where(
              (project) =>
                  project.kind == ProjectKind.unityWorld &&
                  Directory(project.path).existsSync(),
            )
            .toList()
          ..sort((a, b) => b.lastOpenedUtc.compareTo(a.lastOpenedUtc));
    return worlds.isEmpty ? null : worlds.first.path;
  }
}
