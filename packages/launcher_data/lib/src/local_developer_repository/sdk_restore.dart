part of '../local_developer_repository.dart';

extension LocalDeveloperSdkRestore on LocalDeveloperRepository {
  static const _sdkLockName = 'topiaforge.sdk.lock.json';
  static const _sdkIndexName = 'index.json';

  Future<SdkReferencePack?> _initializeSdkProject(String root) async {
    final pack = await _selectSdkPack();
    if (pack == null) {
      if (_looksLikePackagedDeveloperRoot()) {
        throw StateError(
          'The TopiaForge release is missing its SDK reference pack. '
          'Reinstall the developer tools and try again.',
        );
      }
      return null;
    }
    final cached = _cacheSdkPack(pack);
    _writeSdkLock(root, cached);
    _writeProjectGlobalJson(root, cached.dotnetSdkVersion);
    return cached;
  }

  Future<SdkReferencePack?> _restoreProjectSdk(String root) async {
    final lock = _readSdkLock(root);
    if (lock == null) {
      return _initializeSdkProject(root);
    }

    final cached = _loadCachedSdkPack(lock);
    if (cached != null) {
      _writeProjectGlobalJson(root, cached.dotnetSdkVersion);
      return cached;
    }

    final source = await _selectSdkPack(requiredVersion: lock.sdkVersion);
    if (source == null || source.manifestSha256 != lock.manifestSha256) {
      throw StateError(
        'TopiaForge SDK ${lock.sdkVersion} (${lock.manifestSha256.substring(0, 12)}) '
        'is not available. Reinstall the matching TopiaForge developer tools, '
        'or delete $_sdkLockName to intentionally adopt the installed SDK.',
      );
    }
    final restored = _cacheSdkPack(source);
    _writeProjectGlobalJson(root, restored.dotnetSdkVersion);
    return restored;
  }

  Future<void> _restoreNuGetPackages(String root, SdkReferencePack sdk) async {
    final projects =
        Directory(root)
            .listSync(recursive: true, followLinks: false)
            .whereType<File>()
            .where((file) => p.extension(file.path).toLowerCase() == '.csproj')
            .where(
              (file) =>
                  !p.split(file.path).contains('unity-companion') &&
                  !p.split(file.path).contains('obj') &&
                  !p.split(file.path).contains('bin'),
            )
            .toList()
          ..sort((left, right) => left.path.compareTo(right.path));
    if (projects.isEmpty) return;
    if (!sdk.feed.existsSync()) {
      throw StateError(
        'TF1002: The cached TopiaForge NuGet source is missing. Run '
        '`topiaforge restore` again after reinstalling the developer tools. '
        'See https://docs.topiaforge.dev/diagnostics/TF1002',
      );
    }
    final dotnet = await _dotnetSdkResolver(Directory(root));
    for (final project in projects) {
      final restore = await runBoundedProcess(
        dotnet.executable,
        [
          'restore',
          project.path,
          '--use-lock-file',
          '--force-evaluate',
          '--nologo',
        ],
        workingDirectory: root,
        timeout: const Duration(minutes: 5),
        maxStdoutBytes: 8 * 1024 * 1024,
        maxStderrBytes: 8 * 1024 * 1024,
      );
      if (restore.exitCode != 0) {
        throw StateError(
          'TF1003: NuGet restore failed for ${p.basename(project.path)}. '
          '${restore.stdout}\n${restore.stderr}\n'
          'Verify the pinned .NET SDK and rerun `topiaforge restore`. '
          'See https://docs.topiaforge.dev/diagnostics/TF1003',
        );
      }
      final lock = File(p.join(project.parent.path, 'packages.lock.json'));
      if (!lock.existsSync()) {
        throw StateError(
          'TF1004: NuGet did not create ${lock.path}. Ensure the project keeps '
          'RestorePackagesWithLockFile enabled. '
          'See https://docs.topiaforge.dev/diagnostics/TF1004',
        );
      }
    }
  }

  SdkReferencePack? _loadCachedSdkPack(_DeveloperSdkLock lock) {
    final directory = Directory(
      _sdkCachePath(lock.sdkVersion, lock.manifestSha256),
    );
    if (!directory.existsSync()) return null;
    try {
      final pack = SdkReferencePack.load(directory);
      if (pack.version == lock.sdkVersion &&
          pack.gameVersion == lock.gameVersion &&
          pack.dotnetSdkVersion == lock.dotnetSdkVersion &&
          (lock.toolVersion.isEmpty || pack.toolVersion == lock.toolVersion) &&
          pack.manifestSha256 == lock.manifestSha256) {
        return pack;
      }
    } on Object {
      // A packaged source can repair the cache below.
    }
    return null;
  }

  SdkReferencePack _cacheSdkPack(SdkReferencePack source) {
    try {
      final target = Directory(
        _sdkCachePath(source.version, source.manifestSha256),
      );
      if (target.existsSync()) {
        try {
          final cached = SdkReferencePack.load(target);
          if (cached.manifestSha256 == source.manifestSha256) return cached;
        } on Object {
          // Replace a partial or corrupted cache atomically.
        }
      }
      _copyDirectory(source.root, target);
      final cached = SdkReferencePack.load(target);
      if (cached.manifestSha256 != source.manifestSha256) {
        throw StateError('The cached TopiaForge SDK failed verification.');
      }
      return cached;
    } finally {
      final stagingRoot = Directory(p.join(_dataRoot.path, 'sdk-staging'));
      if (p.isWithin(stagingRoot.path, source.root.path) &&
          source.root.existsSync()) {
        source.root.deleteSync(recursive: true);
        if (stagingRoot.existsSync() && stagingRoot.listSync().isEmpty) {
          stagingRoot.deleteSync();
        }
      }
    }
  }

  String _sdkCachePath(String version, String fingerprint) =>
      p.join(_dataRoot.path, 'sdk-cache', version, fingerprint);

  Future<SdkReferencePack?> _selectSdkPack({String? requiredVersion}) async {
    final packaged = _packagedSdkVersion(requiredVersion);
    if (packaged != null) {
      final directory = Directory(
        p.join(_repositoryRoot.path, 'sdk', packaged),
      );
      final pack = SdkReferencePack.load(directory);
      if (requiredVersion != null && pack.version != requiredVersion) {
        return null;
      }
      return pack;
    }
    return _buildSourceSdkPack(requiredVersion: requiredVersion);
  }

  String? _packagedSdkVersion(String? requiredVersion) {
    final sdkRoot = Directory(p.join(_repositoryRoot.path, 'sdk'));
    if (!sdkRoot.existsSync()) return null;
    if (requiredVersion != null) {
      final manifest = File(
        p.join(sdkRoot.path, requiredVersion, SdkReferencePack.manifestName),
      );
      return manifest.existsSync() ? requiredVersion : null;
    }
    final index = File(p.join(sdkRoot.path, _sdkIndexName));
    if (!index.existsSync()) return null;
    final decoded = jsonDecode(
      utf8.decode(
        _readDeveloperFileBoundedSync(
          index,
          maxBytes: _maxDeveloperManifestBytes,
          label: 'SDK reference-pack index',
        ),
      ),
    );
    if (decoded is! Map || decoded['schemaVersion'] != 1) {
      throw StateError('SDK reference-pack index must use schemaVersion 1.');
    }
    final version = decoded['defaultVersion'];
    if (version is! String || version.trim().isEmpty) {
      throw StateError('SDK reference-pack index has no default version.');
    }
    return version;
  }

  Future<SdkReferencePack?> _buildSourceSdkPack({
    String? requiredVersion,
  }) async {
    final projects = _sourceSdkProjects();
    final abstractions = projects['TopiaForge.Mods.Abstractions'];
    if (abstractions == null) return null;
    final sdkVersion = _projectVersion(abstractions.projectFile);
    if (requiredVersion != null && requiredVersion != sdkVersion) return null;
    final dotnetSdkVersion = _repositoryDotnetSdkVersion();

    final missing = projects.values.where((project) {
      if (project.isAnalyzer) return !project.implementation.existsSync();
      return !project.reference.existsSync() ||
          !project.implementation.existsSync() ||
          !project.documentation.existsSync();
    }).toList();
    if (missing.isNotEmpty) {
      final dotnet = await _dotnetSdkResolver(_repositoryRoot);
      for (final project in missing) {
        final build = await runBoundedProcess(
          dotnet.executable,
          [
            'build',
            project.projectFile.path,
            '-c',
            'Release',
            '-p:GenerateDocumentationFile=true',
            if (!project.isAnalyzer) '-p:ProduceReferenceAssembly=true',
          ],
          workingDirectory: _repositoryRoot.path,
          timeout: const Duration(minutes: 5),
          maxStdoutBytes: 8 * 1024 * 1024,
          maxStderrBytes: 8 * 1024 * 1024,
        );
        if (build.exitCode != 0) {
          throw StateError('${build.stdout}\n${build.stderr}'.trim());
        }
      }
    }

    final references = <String, File>{};
    final documentation = <String, File>{};
    final analyzers = <String, File>{};
    final runtimeAssemblies = <String, File>{};
    final runtimeSupportAssemblies = <String, List<File>>{};
    final packageDependencies = <String, List<String>>{};
    final buildTransitiveProps = <String, File>{};
    final buildTransitiveTargets = <String, File>{};
    for (final project in projects.values) {
      if (_projectVersion(project.projectFile) != sdkVersion) {
        throw StateError(
          '${project.name} must use SDK package version $sdkVersion.',
        );
      }
      if (project.isAnalyzer) {
        analyzers[project.name] = project.implementation;
      } else {
        references[project.name] = project.reference;
        documentation[project.name] = project.documentation;
        packageDependencies[project.name] = project.dependencies;
        if (project.name == 'TopiaForge.Mods.Testing') {
          runtimeAssemblies[project.name] = project.implementation;
        }
      }
      final props = File(
        p.join(
          project.projectFile.parent.path,
          'buildTransitive',
          '${project.name}.props',
        ),
      );
      final targets = File(
        p.join(
          project.projectFile.parent.path,
          'buildTransitive',
          '${project.name}.targets',
        ),
      );
      if (props.existsSync()) buildTransitiveProps[project.name] = props;
      if (targets.existsSync()) buildTransitiveTargets[project.name] = targets;
    }
    runtimeSupportAssemblies['TopiaForge.Mods.Testing'] = [
      for (final packageId in topiaForgeTestingRuntimeSupportPackageIds)
        projects[packageId]!.implementation,
    ];

    final staging = Directory(
      p.join(
        _dataRoot.path,
        'sdk-staging',
        '$pid-${DateTime.now().microsecondsSinceEpoch}',
      ),
    );
    try {
      return const SdkReferencePackWriter().write(
        destination: staging,
        sdkVersion: sdkVersion,
        dotnetSdkVersion: dotnetSdkVersion,
        toolVersion: _repositoryToolVersion(),
        references: references,
        documentation: documentation,
        analyzers: analyzers,
        runtimeAssemblies: runtimeAssemblies,
        runtimeSupportAssemblies: runtimeSupportAssemblies,
        packageDependencies: packageDependencies,
        buildTransitiveProps: buildTransitiveProps,
        buildTransitiveTargets: buildTransitiveTargets,
      );
    } catch (_) {
      if (staging.existsSync()) staging.deleteSync(recursive: true);
      rethrow;
    }
  }

  Map<String, _SourceSdkProject> _sourceSdkProjects() {
    final result = <String, _SourceSdkProject>{};
    final source = Directory(p.join(_repositoryRoot.path, 'src'));
    if (!source.existsSync()) return result;
    for (final directory in source.listSync().whereType<Directory>()) {
      final name = p.basename(directory.path);
      if (!topiaForgeSdkPackageIds.contains(name)) {
        continue;
      }
      final project = File(p.join(directory.path, '$name.csproj'));
      if (!project.existsSync()) continue;
      final text = utf8.decode(
        _readDeveloperFileBoundedSync(
          project,
          maxBytes: _maxDeveloperManifestBytes,
          label: 'SDK project',
        ),
      );
      final targetFramework = RegExp(
        r'<TargetFramework>\s*([^<]+?)\s*</TargetFramework>',
      ).firstMatch(text)?.group(1);
      if (targetFramework == null) {
        throw StateError('$name does not declare a TargetFramework.');
      }
      final dependencies = <String>{};
      for (final match in RegExp(
        r'<ProjectReference\s+Include="([^"]+)"',
      ).allMatches(text)) {
        final dependency = p.basenameWithoutExtension(
          match.group(1)!.replaceAll('\\', '/'),
        );
        if (dependency.startsWith('TopiaForge.Mods.') &&
            dependency != 'TopiaForge.Mods.Analyzers') {
          dependencies.add(dependency);
        }
      }
      result[name] = _SourceSdkProject(
        name: name,
        projectFile: project,
        targetFramework: targetFramework,
        dependencies: dependencies.toList()..sort(),
      );
    }
    return result;
  }

  String _projectVersion(File project) {
    final text = utf8.decode(
      _readDeveloperFileBoundedSync(
        project,
        maxBytes: _maxDeveloperManifestBytes,
        label: 'SDK project',
      ),
    );
    for (final property in const ['PackageVersion', 'Version']) {
      final match = RegExp(
        '<$property>\\s*([^<]+?)\\s*</$property>',
      ).firstMatch(text);
      if (match != null) return match.group(1)!.trim();
    }
    throw StateError('SDK project has no Version or PackageVersion.');
  }

  String _repositoryDotnetSdkVersion() {
    final global = File(p.join(_repositoryRoot.path, 'global.json'));
    final decoded = jsonDecode(
      utf8.decode(
        _readDeveloperFileBoundedSync(
          global,
          maxBytes: _maxDeveloperManifestBytes,
          label: 'global.json',
        ),
      ),
    );
    if (decoded is! Map || decoded['sdk'] is! Map) {
      throw StateError('global.json must contain an sdk object.');
    }
    final version = (decoded['sdk'] as Map)['version'];
    if (version is! String || version.trim().isEmpty) {
      throw StateError('global.json must pin an exact .NET SDK version.');
    }
    return version;
  }

  String _repositoryToolVersion() {
    final pubspec = File(
      p.join(_repositoryRoot.path, 'apps', 'topiaforge_cli', 'pubspec.yaml'),
    );
    if (!pubspec.existsSync()) return '';
    final text = utf8.decode(
      _readDeveloperFileBoundedSync(
        pubspec,
        maxBytes: _maxDeveloperManifestBytes,
        label: 'TopiaForge CLI pubspec',
      ),
    );
    final match = RegExp(
      r'^version:\s*(\S+)\s*$',
      multiLine: true,
    ).firstMatch(text);
    return match?.group(1) ?? '';
  }

  void _writeSdkLock(String root, SdkReferencePack pack) {
    _writeDeveloperTextAtomic(
      File(p.join(root, _sdkLockName)),
      '${_prettyJson({'schemaVersion': 1, 'sdkVersion': pack.version, 'gameVersion': pack.gameVersion, 'manifestSha256': pack.manifestSha256, 'dotnetSdkVersion': pack.dotnetSdkVersion, if (pack.toolVersion.isNotEmpty) 'toolVersion': pack.toolVersion})}\n',
    );
  }

  _DeveloperSdkLock? _readSdkLock(String root) {
    final file = File(p.join(root, _sdkLockName));
    if (!file.existsSync()) return null;
    final decoded = jsonDecode(
      utf8.decode(
        _readDeveloperFileBoundedSync(
          file,
          maxBytes: _maxDeveloperManifestBytes,
          label: _sdkLockName,
        ),
      ),
    );
    if (decoded is! Map || decoded['schemaVersion'] != 1) {
      throw StateError('$_sdkLockName must use schemaVersion 1.');
    }
    return _DeveloperSdkLock(
      sdkVersion: decoded['sdkVersion'] as String? ?? '',
      gameVersion: decoded['gameVersion'] as String? ?? '',
      manifestSha256: decoded['manifestSha256'] as String? ?? '',
      dotnetSdkVersion: decoded['dotnetSdkVersion'] as String? ?? '',
      toolVersion: decoded['toolVersion'] as String? ?? '',
    )..validate();
  }

  void _writeProjectGlobalJson(String root, String dotnetSdkVersion) {
    _writeDeveloperTextAtomic(
      File(p.join(root, 'global.json')),
      '${_prettyJson({
        'sdk': {'version': dotnetSdkVersion, 'rollForward': 'disable', 'allowPrerelease': false},
      })}\n',
    );
  }

  bool _looksLikePackagedDeveloperRoot() =>
      !File(p.join(_repositoryRoot.path, 'TopiaForge.slnx')).existsSync() &&
      Directory(p.join(_repositoryRoot.path, 'tools')).existsSync() &&
      Directory(p.join(_repositoryRoot.path, 'templates')).existsSync() &&
      Directory(p.join(_repositoryRoot.path, 'dist')).existsSync();
}
