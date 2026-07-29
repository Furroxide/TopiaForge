part of '../local_developer_repository.dart';

/// Directory-template mod scaffolding from `templates/mod/<id>/`, with manifests generated programmatically from
/// template defaults and CLI overrides so every fresh scaffold passes `check package`.
extension LocalDeveloperModScaffolding on LocalDeveloperRepository {
  static const _sdkModulePackagesByRuntimeDependency = <String, String>{
    'io.github.furroxide.topiaforge.chronos': 'TopiaForge.Mods.Chronos',
    'io.github.furroxide.topiaforge.creatorcontent':
        'TopiaForge.Mods.CreatorContent',
    'io.github.furroxide.topiaforge.prompts': 'TopiaForge.Mods.Prompts',
    'io.github.furroxide.topiaforge.robotkit': 'TopiaForge.Mods.RobotKit',
    'io.github.furroxide.topiaforge.multiplayer': 'TopiaForge.Mods.Multiplayer',
    'io.github.furroxide.topiaforge.ugc.livesync': 'TopiaForge.Mods.Ugc',
    'io.github.furroxide.topiaforge.worlds': 'TopiaForge.Mods.Worlds',
  };
  Directory get _modTemplatesRoot =>
      Directory(p.join(_repositoryRoot.path, 'templates', 'mod'));

  static const _textTemplateExtensions = {
    '.cs',
    '.csproj',
    '.md',
    '.json',
    '.asmdef',
    '.txt',
    '.props',
    '.targets',
    '.xml',
    '.yml',
    '.yaml',
  };

  Future<List<ModTemplateInfo>> _listModTemplates() async {
    final templates = <ModTemplateInfo>[];
    final root = _modTemplatesRoot;
    if (root.existsSync()) {
      for (final entry in root.listSync().whereType<Directory>()) {
        final metaFile = File(p.join(entry.path, 'template.json'));
        if (!metaFile.existsSync()) {
          continue;
        }
        try {
          final info = ModTemplateInfo.fromJson(
            jsonDecode(
                  utf8.decode(
                    await _readDeveloperFileBounded(
                      metaFile,
                      maxBytes: _maxDeveloperManifestBytes,
                      label: 'Mod template metadata',
                    ),
                  ),
                )
                as Map<String, Object?>,
          );
          if (info.id.isNotEmpty) {
            templates.add(info);
          }
        } on Object {
          // Skip malformed template metadata; the rest stay usable.
        }
      }
    }
    if (!templates.any((template) => template.id == 'minimal')) {
      templates.add(
        const ModTemplateInfo(
          id: 'minimal',
          label: 'Minimal mod',
          description: 'Hello-world mod that logs scene and update events.',
        ),
      );
    }
    templates.sort((a, b) => a.id.compareTo(b.id));
    return templates;
  }

  /// Scaffolds the mod source files + `topiaforge.mod.json` into [root]. Falls back to the built-in minimal
  /// generator when the requested template directory is absent (synthetic test environments).
  Future<void> _scaffoldModFromTemplate(
    String root,
    String id,
    String name,
    ModScaffoldOptions options,
    bool includeUnityCompanion,
  ) async {
    final templateDir = Directory(
      p.join(_modTemplatesRoot.path, options.template),
    );
    final hasTemplateDir =
        templateDir.existsSync() &&
        File(p.join(templateDir.path, 'template.json')).existsSync();
    if (!hasTemplateDir && options.template != 'minimal') {
      final available = (await _listModTemplates())
          .map((template) => template.id)
          .join(', ');
      throw StateError(
        'Unknown mod template "${options.template}" (available: $available).',
      );
    }

    final tokens = _modTemplateTokens(id, name);
    ModTemplateInfo? info;
    if (hasTemplateDir) {
      info = await _instantiateModTemplate(templateDir, root, tokens);
    } else {
      await _writeStarterModSources(root, id, name);
    }

    // Base manifest ← template defaults ← author's explicit overrides,
    // followed by a ModManifest round-trip for canonical field names/ordering.
    // Identity and license deliberately remain non-publishable until chosen by
    // the author; templates must never claim ownership on their behalf.
    var manifestMap = <String, Object?>{
      r'$schema': ModManifest.canonicalSchemaUrl,
      'schemaVersion': ModManifest.currentSchemaVersion,
      'name': id,
      'displayName': name,
      'version': '0.1.0',
      'author': {'name': TopiaForgeScaffoldDefaults.authorName},
      'description': '',
      'entryAssembly': '${tokens['ASSEMBLY_NAME']}.dll',
      'entryType': '${tokens['ASSEMBLY_NAME']}.${tokens['TYPE_NAME']}Mod',
      'dependencies': <String, Object?>{},
      'optionalDependencies': <String, Object?>{},
      'supportedGameVersionRange': '0.0.2309',
      'supportedLoaderVersionRange': _compatibleMinorRange(
        TopiaForgeRuntimeVersions.loaderVersion,
      ),
      'supportedSdkVersionRange': _compatibleMinorRange(
        TopiaForgeRuntimeVersions.sdkVersion,
      ),
      'apiAssemblies': <Object?>[],
      'license': TopiaForgeScaffoldDefaults.license,
    };
    if (info != null && info.manifestDefaults.isNotEmpty) {
      manifestMap = {...manifestMap, ...info.manifestDefaults};
    }
    if (options.authorName == null &&
        options.authorEmail == null &&
        options.authorUrl == null) {
      manifestMap['author'] = {'name': TopiaForgeScaffoldDefaults.authorName};
    }
    if (options.license == null) {
      manifestMap['license'] = TopiaForgeScaffoldDefaults.license;
    }
    manifestMap = options.applyTo(manifestMap);
    final manifest = ModManifest.fromJson(manifestMap);
    _writeDeveloperTextAtomic(
      File(p.join(root, 'topiaforge.mod.json')),
      _prettyJson(manifest.toJson()),
    );
    _writeScaffoldLicense(root, manifest, options.licenseText);

    if (includeUnityCompanion) {
      await _writeUnityCompanionScaffold(root, name);
    }
  }

  Future<void> _ensureSdkPackageReferences(
    String root,
    SdkReferencePack sdk,
  ) async {
    final manifest = await _readModManifest(root);
    final modulePackages = manifest.dependencies
        .map(
          (dependency) => _sdkModulePackagesByRuntimeDependency[dependency.id],
        )
        .whereType<String>()
        .where(sdk.containsPackage)
        .toSet();
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
    for (final project in projects) {
      final isTest = p
          .basenameWithoutExtension(project.path)
          .endsWith('.Tests');
      final packages = <String>{
        'TopiaForge.Mods.Abstractions',
        ...modulePackages,
      };
      if (!isTest && sdk.containsPackage('TopiaForge.Mods.Analyzers')) {
        packages.add('TopiaForge.Mods.Analyzers');
      }
      if (isTest && sdk.containsPackage('TopiaForge.Mods.Testing')) {
        packages.add('TopiaForge.Mods.Testing');
      }
      final original = await project.readAsString();
      final updated = _withExactSdkPackageReferences(
        original,
        packages: packages,
        version: sdk.version,
        safeProject: !isTest,
        devPropsPath: p.posix.joinAll(
          p.split(
            p.relative(
              p.join(root, 'topiaforge.dev.props'),
              from: project.parent.path,
            ),
          ),
        ),
      );
      if (updated != original) {
        _writeDeveloperTextAtomic(project, updated);
      }
    }
  }

  String _compatibleMinorRange(String version) {
    final parsed = SemanticVersion.parse(version);
    return '>=$parsed '
        '<${parsed.major}.${parsed.minor + 1}.0';
  }

  Map<String, String> _modTemplateTokens(String id, String name) {
    final assembly = _assemblyName(id);
    return {
      'MOD_ID': id,
      'DISPLAY_NAME': name,
      'ASSEMBLY_NAME': assembly,
      'TYPE_NAME': _typeName(id),
      // The same derivation `topiaforge world link` uses, so a scaffolded world mod and its paired Unity
      // project agree on the bundle name out of the box.
      'BUNDLE_NAME': WorldAuthoringConfig.deriveBundleName(id),
      'SDK_VERSION': TopiaForgeRuntimeVersions.sdkVersion,
    };
  }

  /// Copies [templateDir] into [root], substituting `{{TOKEN}}` markers in both file paths and text-file
  /// contents. `template.json` (metadata) and any `topiaforge.mod.json` (generated separately) are skipped.
  /// Returns the parsed template metadata with tokens substituted in its manifest defaults.
  Future<ModTemplateInfo> _instantiateModTemplate(
    Directory templateDir,
    String root,
    Map<String, String> tokens,
  ) async {
    String substitute(String value) {
      var result = value;
      tokens.forEach((key, replacement) {
        result = result.replaceAll('{{$key}}', replacement);
      });
      return result;
    }

    for (final entity in templateDir.listSync(recursive: true)) {
      final relative = p.relative(entity.path, from: templateDir.path);
      final base = p.basename(relative);
      if (base == 'template.json' || base == 'topiaforge.mod.json') {
        continue;
      }
      final target = p.join(root, substitute(relative));
      if (entity is Directory) {
        Directory(target).createSync(recursive: true);
      } else if (entity is File) {
        File(target).createSync(recursive: true);
        if (_textTemplateExtensions.contains(
          p.extension(entity.path).toLowerCase(),
        )) {
          final content = utf8.decode(
            await _readDeveloperFileBounded(
              entity,
              maxBytes: _maxDeveloperCatalogBytes,
              label: 'Mod template text file',
            ),
          );
          await File(target).writeAsString(substitute(content));
        } else {
          entity.copySync(target);
        }
      }
    }

    final metaRaw = utf8.decode(
      await _readDeveloperFileBounded(
        File(p.join(templateDir.path, 'template.json')),
        maxBytes: _maxDeveloperManifestBytes,
        label: 'Mod template metadata',
      ),
    );
    return ModTemplateInfo.fromJson(
      jsonDecode(substitute(metaRaw)) as Map<String, Object?>,
    );
  }

  Future<ModManifest> _readModManifest(String projectPath) async {
    final root = _requireProjectRoot(projectPath);
    final file = File(p.join(root.path, 'topiaforge.mod.json'));
    if (!file.existsSync()) {
      throw StateError(
        'topiaforge.mod.json was not found in ${root.path}. Run from a mod '
        'directory or pass --project <dir>.',
      );
    }
    return ModManifest.fromJson(
      jsonDecode(
            utf8.decode(
              await _readDeveloperFileBounded(
                file,
                maxBytes: _maxDeveloperManifestBytes,
                label: 'topiaforge.mod.json',
              ),
            ),
          )
          as Map<String, Object?>,
    );
  }

  Future<List<LauncherIssue>> _updateModManifest(
    String projectPath,
    ModManifest manifest,
  ) async {
    final root = _requireProjectRoot(projectPath);
    _writeDeveloperTextAtomic(
      File(p.join(root.path, 'topiaforge.mod.json')),
      _prettyJson(manifest.toJson()),
    );
    return manifest.validate();
  }

  Directory get _companionTemplateDir => Directory(
    p.join(
      _repositoryRoot.path,
      'templates',
      'unity-companion',
      'Packages',
      'io.github.furroxide.topiaforge.ugc-companion',
    ),
  );

  Future<bool> _ensureUgcCompanionPackage(
    String projectPath, {
    bool update = false,
  }) async {
    final target = Directory(
      p.join(
        projectPath,
        'Packages',
        'io.github.furroxide.topiaforge.ugc-companion',
      ),
    );
    if (target.existsSync() && !update) {
      return true;
    }
    final source = _companionTemplateDir;
    if (!source.existsSync()) {
      return target.existsSync();
    }
    _copyDirectory(source, target);
    return true;
  }

  Future<String> _writeUgcCompanionSeed(
    String projectPath, {
    required String watchFolder,
    String projectName = '',
    String sceneId = '',
    String sceneName = '',
    String environment = '',
    bool liveSync = true,
  }) async {
    final settingsDir = Directory(p.join(projectPath, 'ProjectSettings'))
      ..createSync(recursive: true);
    final file = File(p.join(settingsDir.path, 'TopiaForgeUgcCompanion.json'));
    _writeDeveloperTextAtomic(
      file,
      _prettyJson({
        'schemaVersion': 2,
        'watchFolder': watchFolder,
        if (projectName.isNotEmpty) 'projectName': projectName,
        if (sceneId.isNotEmpty) 'sceneId': sceneId,
        if (sceneName.isNotEmpty) 'sceneName': sceneName,
        if (environment.isNotEmpty) 'environment': environment,
        'liveSync': liveSync,
        // The companion's editor bootstrap applies the seed once and stamps appliedUtc; a fresh seededUtc from
        // the CLI re-arms it.
        'seededUtc': DateTime.now().toUtc().toIso8601String(),
      }),
    );
    return file.path;
  }
}

String _withExactSdkPackageReferences(
  String project, {
  required Set<String> packages,
  required String version,
  required bool safeProject,
  required String devPropsPath,
}) {
  var result = project;
  final projectElement = RegExp(r'<Project(?:\s[^>]*)?>').firstMatch(result);
  if (projectElement == null) {
    throw StateError('TopiaForge C# project has no Project element.');
  }
  if (!RegExp(
    r'<Import\s+Project="[^"]*topiaforge\.dev\.props"',
  ).hasMatch(result)) {
    final offset = projectElement.end;
    result =
        '${result.substring(0, offset)}\n'
        '  <Import Project="$devPropsPath" '
        'Condition="Exists(\'$devPropsPath\')" />'
        '${result.substring(offset)}';
  }
  final safePattern = RegExp(
    r'<TopiaForgeSafeProject(?:\s[^>]*)?>[^<]*</TopiaForgeSafeProject>',
  );
  if (safePattern.hasMatch(result)) {
    result = result.replaceFirst(
      safePattern,
      '<TopiaForgeSafeProject>${safeProject ? 'true' : 'false'}</TopiaForgeSafeProject>',
    );
  }
  final properties = <String, String>{
    'RestorePackagesWithLockFile': 'true',
    'RestoreLockedMode':
        "\$(ContinuousIntegrationBuild) == 'true' and Exists('\$(MSBuildProjectDirectory)/packages.lock.json')",
    'TopiaForgeSafeProject': safeProject ? 'true' : 'false',
  };
  final firstPropertyGroup = RegExp(
    r'<PropertyGroup(?:\s[^>]*)?>',
  ).firstMatch(result);
  if (firstPropertyGroup == null) {
    throw StateError('TopiaForge C# project has no PropertyGroup.');
  }
  final propertyLines = <String>[];
  for (final entry in properties.entries) {
    final existing = RegExp('<${entry.key}(?:\\s[^>]*)?>');
    if (existing.hasMatch(result)) continue;
    if (entry.key == 'RestoreLockedMode') {
      propertyLines.add(
        '    <RestoreLockedMode Condition="${entry.value}">true</RestoreLockedMode>',
      );
    } else {
      propertyLines.add('    <${entry.key}>${entry.value}</${entry.key}>');
    }
  }
  if (propertyLines.isNotEmpty) {
    final offset = firstPropertyGroup.end;
    result =
        '${result.substring(0, offset)}\n${propertyLines.join('\n')}'
        '${result.substring(offset)}';
  }

  final missing = <String>[];
  for (final package in packages.toList()..sort()) {
    final pattern = RegExp(
      '<PackageReference\\s+Include=["\\\']${RegExp.escape(package)}["\\\'][^>]*(?:/>|>.*?</PackageReference>)',
      dotAll: true,
    );
    final match = pattern.firstMatch(result);
    if (match == null) {
      missing.add(package);
      continue;
    }
    var item = match.group(0)!;
    final versionPattern = RegExp(r'\sVersion="[^"]*"');
    if (versionPattern.hasMatch(item)) {
      item = item.replaceFirst(versionPattern, ' Version="$version"');
    } else {
      item = item.replaceFirst(
        '<PackageReference ',
        '<PackageReference Version="$version" ',
      );
    }
    if (topiaForgeAnalyzerPackageIds.contains(package) &&
        !item.contains('PrivateAssets=')) {
      item = item.replaceFirst(
        '<PackageReference ',
        '<PackageReference PrivateAssets="all" ',
      );
    }
    result = result.replaceRange(match.start, match.end, item);
  }
  if (missing.isNotEmpty) {
    final items = missing
        .map((package) {
          final private = topiaForgeAnalyzerPackageIds.contains(package)
              ? ' PrivateAssets="all"'
              : '';
          return '    <PackageReference Include="$package" Version="$version"$private />';
        })
        .join('\n');
    final close = result.lastIndexOf('</Project>');
    if (close < 0) throw StateError('TopiaForge C# project is malformed.');
    result =
        '${result.substring(0, close)}  <ItemGroup>\n$items\n'
        '  </ItemGroup>\n${result.substring(close)}';
  }
  return result;
}
