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
        '  topiaforge world link --project <unityProj> --mod <modDir> [--world id] [--bundle name] [--prefab assetPath]\n'
        '  topiaforge world build [--project <unityProj|name>] [--mod <modDir>] [--bundle name] [--unity Unity.exe] [--dry-run]\n'
        '  topiaforge world play --target <launchTargetId> [--project <unityProj|name>] [--mod <modDir>] '
        '[--bundle name] [--unity Unity.exe] [--configuration cfg] [--game-dir path] [--profile id] '
        '[--world id] [--transition scene-replacement|additive-arena] [--no-wait | --wait-seconds 1..300]',
      ),
    };
  }

  Future<int> _worldLink(List<String> args) async {
    final projectArg = _option(args, '--project');
    final modArg = _option(args, '--mod');
    if (projectArg == null || modArg == null) {
      throw UsageError(
        'Usage: topiaforge world link --project <unityProj> --mod <modDir> [--world id] [--bundle name] [--prefab assetPath]',
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
    final requestedWorld = _option(args, '--world');
    final worlds =
        (manifest.contributions?.worlds ?? const <ModWorldDeclaration>[])
            .where(
              (world) =>
                  world.content?.kind == ModWorldContent.bundleKind &&
                  (requestedWorld == null || world.id == requestedWorld),
            )
            .toList();
    if (worlds.length != 1) {
      throw StateError(
        'Choose one declared bundle world with --world <id>. '
        'This mod has ${worlds.length} matching declarations; edit contributions.worlds if none exists.',
      );
    }
    final world = worlds.single;
    final bundle = p.posix.basenameWithoutExtension(world.content!.bundle);
    if (world.content!.bundle != 'AssetBundles/$bundle.bundle') {
      throw StateError(
        'World authoring builds into AssetBundles/<name>.bundle. '
        'Update ${world.id} content.bundle to that location before linking.',
      );
    }
    final requestedBundle = _option(args, '--bundle');
    final requestedPrefab = _option(args, '--prefab');
    if ((requestedBundle != null && requestedBundle != bundle) ||
        (requestedPrefab != null &&
            requestedPrefab.toLowerCase() !=
                world.content!.prefab.toLowerCase())) {
      throw StateError(
        'Bundle and prefab overrides must match ${world.id} content declaration. '
        'Update contributions.worlds before linking different content.',
      );
    }

    final config = await developerRepository.writeWorldAuthoringConfig(
      project,
      WorldAuthoringConfig(
        worldId: world.id,
        bundleName: bundle,
        worldPrefab: requestedPrefab ?? world.content!.prefab,
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
    final options = _parseLaunchOptions(
      args,
      extraValues: const {
        '--project',
        '--mod',
        '--bundle',
        '--unity',
        '--configuration',
      },
    );
    final request = options.selectionOverride?.request;
    if (request == null) {
      throw UsageError(
        'world play requires --target <declared launch target>. '
        'The saved profile cannot identify which authored world to test.',
      );
    }
    final project = await _resolveUnityDevProject(_option(args, '--project'));
    if (project == null) {
      throw StateError(
        'No Unity world project found. Pass --project <path|name>.',
      );
    }
    final config = await developerRepository.readWorldAuthoringConfig(project);
    final modRaw = _option(args, '--mod') ?? config?.modPath ?? '';
    if (modRaw.isEmpty) {
      throw StateError('No paired mod. Run world link or pass --mod.');
    }
    final mod = p.normalize(
      p.isAbsolute(modRaw) ? modRaw : p.join(project, modRaw),
    );
    final manifest = await developerRepository.readModManifest(mod);
    if (!(manifest.contributions?.launchTargets ??
            const <ModLaunchTargetDeclaration>[])
        .any((target) => target.id == request.targetId)) {
      throw StateError(
        '${request.targetId} is not a launch target declared by the paired mod. '
        'Choose a contributions.launchTargets id from its manifest.',
      );
    }
    if (!await _ensureBuildTooling()) return 1;
    final build = await developerRepository.buildWorldBundle(
      unityProjectPath: project,
      modPath: mod,
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
    final configuration = _option(args, '--configuration') ?? 'Release';
    final packagePath =
        File(p.join(mod, 'topiaforge.project.json')).existsSync()
        ? await developerRepository.packProject(
            mod,
            configuration: configuration,
          )
        : await developerRepository.packModDirectory(
            mod,
            configuration: configuration,
          );
    stdout.writeln('Packed $packagePath.');
    final launcher = LocalLauncherRepository(
      knownGamePath: options.gameDirectory,
    );
    try {
      final install = await launcher.detectKnownInstall();
      if (install == null) throw StateError(_noInstallRemedy);
      await launcher.installPackage(packagePath, install);
      stdout.writeln('Installed $packagePath.');
      final snapshot = await launcher.loadSnapshot();
      return await _runLaunchWorkflow(
        launcher,
        install,
        _launchProfile(snapshot, options),
        options,
      );
    } finally {
      await launcher.dispose();
    }
  }

  Future<String?> _resolveUnityDevProject(String? selector) async {
    bool isUnityProject(String path) =>
        Directory(p.join(path, 'ProjectSettings')).existsSync() &&
        Directory(p.join(path, 'Assets')).existsSync();

    if (selector != null) {
      // An explicit path has to clear the same check `world link` applies; every
      // other route into this resolver already does. A directory that fails it is
      // still offered to the registry lookup, because `--project` also takes a name
      // and a same-named directory in the working tree must not shadow one.
      final directory = Directory(selector).existsSync()
          ? p.normalize(p.absolute(selector))
          : null;
      if (directory != null && isUnityProject(directory)) {
        return directory;
      }
      final projects = await developerRepository.listProjects();
      for (final project in projects) {
        if (project.name.toLowerCase() == selector.toLowerCase() &&
            project.isUnity) {
          return project.path;
        }
      }
      if (directory != null) {
        // Say why a real directory was rejected rather than letting the caller's
        // "no project found" message suggest the path was missing.
        throw StateError(
          '$directory is not a Unity project (expected Assets/ and ProjectSettings/).',
        );
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
