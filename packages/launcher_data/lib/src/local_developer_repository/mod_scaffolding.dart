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
    'io.github.furroxide.topiaforge.worlds': 'TopiaForge.Mods.Worlds',
  };
  static const _multiplayerGeneratorPackage =
      'TopiaForge.Mods.Multiplayer.Generators';
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

  Future<void> _ensureSdkPackageReferences(
    String root,
    SdkReferencePack sdk,
  ) async {
    final modulePackages = await _sdkModulePackages(root, sdk);
    final projects = _sdkPinnedProjects(root);
    final texts = <String, String>{
      for (final project in projects)
        project.path: await project.readAsString(),
    };
    // A test project that compiles the mod's own sources compiles its
    // multiplayer contract with them, so it needs the generator that writes
    // the contract's other half whenever the mod project has it. Without
    // it, every generated member the mod calls is missing there (CS0103).
    final sourcesNeedGenerator =
        sdk.containsPackage(_multiplayerGeneratorPackage) &&
        projects.any(
          (project) =>
              !_isSdkTestProject(project) &&
              includedPackageReferences(texts[project.path]!, const [
                _multiplayerGeneratorPackage,
              ]).isNotEmpty,
        );
    for (final project in projects) {
      final isTest = _isSdkTestProject(project);
      final original = texts[project.path]!;
      final updated = _withExactSdkPackageReferences(
        original,
        packages: {
          ..._pinnedSdkPackages(project, original, sdk, modulePackages),
          if (isTest &&
              sourcesNeedGenerator &&
              _compilesSourcesFromOutside(original, project.parent.path))
            _multiplayerGeneratorPackage,
        },
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

  /// The mod projects under [root] whose SDK packages are pinned: every
  /// `.csproj` outside `unity-companion`, `obj` and `bin`, in path order.
  List<File> _sdkPinnedProjects(String root) =>
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

  bool _isSdkTestProject(File project) =>
      p.basenameWithoutExtension(project.path).endsWith('.Tests');

  /// The SDK packages that the dependencies in [root]'s manifest imply, of
  /// those [sdk] holds.
  Future<Set<String>> _sdkModulePackages(
    String root,
    SdkReferencePack sdk,
  ) async {
    final manifest = await _readModManifest(root);
    return manifest.dependencies
        .map(
          (dependency) => _sdkModulePackagesByRuntimeDependency[dependency.id],
        )
        .whereType<String>()
        .where(sdk.containsPackage)
        .toSet();
  }

  /// The SDK packages [project], whose text is [text], is pinned to [sdk]'s
  /// version.
  Set<String> _pinnedSdkPackages(
    File project,
    String text,
    SdkReferencePack sdk,
    Set<String> modulePackages,
  ) {
    final isTest = _isSdkTestProject(project);
    final sdkPackages = sdk.packages
        .map((package) => package.id)
        .where(sdk.containsPackage)
        .toList();
    return {
      'TopiaForge.Mods.Abstractions',
      ...modulePackages,
      // The feed holds no other version of an SDK package, so every SDK item
      // the project has moves with the SDK too, including those the
      // manifest's dependencies do not imply: the multiplayer generator, the
      // Unity interop contracts, or an optional module.
      ...includedPackageReferences(text, sdkPackages),
      if (!isTest && sdk.containsPackage('TopiaForge.Mods.Analyzers'))
        'TopiaForge.Mods.Analyzers',
      if (isTest && sdk.containsPackage('TopiaForge.Mods.Testing'))
        'TopiaForge.Mods.Testing',
    };
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

  /// [templateDir]'s `template.json`, with `{{TOKEN}}` markers substituted
  /// in its manifest defaults.
  Future<ModTemplateInfo> _readModTemplateInfo(
    Directory templateDir,
    Map<String, String> tokens,
  ) async {
    final metaRaw = utf8.decode(
      await _readDeveloperFileBounded(
        File(p.join(templateDir.path, 'template.json')),
        maxBytes: _maxDeveloperManifestBytes,
        label: 'Mod template metadata',
      ),
    );
    return ModTemplateInfo.fromJson(
      jsonDecode(_substituteModTemplateTokens(metaRaw, tokens))
          as Map<String, Object?>,
    );
  }

  /// Copies [templateDir] into [root], substituting `{{TOKEN}}` markers in both file paths and text-file
  /// contents. `template.json` (metadata) and any `topiaforge.mod.json` (generated separately) are skipped.
  /// A path that leaves [root] once its tokens are substituted is refused: a token such as the display name is
  /// the author's own text.
  Future<void> _instantiateModTemplate(
    Directory templateDir,
    String root,
    Map<String, String> tokens,
  ) async {
    final base = p.normalize(p.absolute(root));
    for (final entity in templateDir.listSync(recursive: true)) {
      final relative = p.relative(entity.path, from: templateDir.path);
      final name = p.basename(relative);
      if (name == 'template.json' || name == 'topiaforge.mod.json') {
        continue;
      }
      final substituted = _substituteModTemplateTokens(relative, tokens);
      final target = p.normalize(p.join(base, substituted));
      if (!p.isWithin(base, target)) {
        throw StateError(
          'The mod template path "$relative" leaves the project folder once '
          'its tokens are substituted: "$substituted". Choose a mod name '
          'without ".." or a leading path separator.',
        );
      }
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
          await File(
            target,
          ).writeAsString(_substituteModTemplateTokens(content, tokens));
        } else {
          entity.copySync(target);
        }
      }
    }
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
}

/// [value] with every `{{KEY}}` of [tokens] replaced by its value.
String _substituteModTemplateTokens(String value, Map<String, String> tokens) {
  var result = value;
  tokens.forEach((key, replacement) {
    result = result.replaceAll('{{$key}}', replacement);
  });
  return result;
}

String _withExactSdkPackageReferences(
  String project, {
  required Set<String> packages,
  required String version,
  required bool safeProject,
  required String devPropsPath,
}) {
  var result = project;
  final projectElement = firstStartTag(result, 'Project');
  if (projectElement == null) {
    throw StateError('TopiaForge C# project has no Project element.');
  }
  if (!_importsDevProps(result)) {
    final offset = projectElement.end;
    result =
        '${result.substring(0, offset)}\n'
        '  <Import Project="$devPropsPath" '
        'Condition="Exists(\'$devPropsPath\')" />'
        '${result.substring(offset)}';
  }
  final safeElement = firstTextElement(result, 'TopiaForgeSafeProject');
  if (safeElement != null) {
    result = result.replaceRange(
      safeElement.start,
      safeElement.end,
      '<TopiaForgeSafeProject>${safeProject ? 'true' : 'false'}</TopiaForgeSafeProject>',
    );
  }
  final properties = <String, String>{
    'RestorePackagesWithLockFile': 'true',
    'RestoreLockedMode':
        "\$(ContinuousIntegrationBuild) == 'true' and Exists('\$(MSBuildProjectDirectory)/packages.lock.json')",
    'TopiaForgeSafeProject': safeProject ? 'true' : 'false',
  };
  final firstPropertyGroup = firstStartTag(result, 'PropertyGroup');
  if (firstPropertyGroup == null) {
    throw StateError('TopiaForge C# project has no PropertyGroup.');
  }
  final propertyLines = <String>[];
  for (final entry in properties.entries) {
    if (firstStartTag(result, entry.key) != null) continue;
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

  return withPinnedSdkPackageReferences(
    result,
    packages: packages,
    version: version,
  );
}

/// Whether [project] has an `<Import>` outside comments whose `Project`
/// attribute, in either quote and among any others, ends in
/// `topiaforge.dev.props`.
bool _importsDevProps(String project) => startTags(project, 'Import').any(
  (found) => found.tag.attributes.any((attribute) {
    if (!isAttributeNamed(project, attribute, 'Project')) return false;
    final value = trimmedAttributeValue(project, attribute);
    return project
        .substring(value.start, value.end)
        .endsWith('topiaforge.dev.props');
  }),
);

/// Whether [project], the text of the project file in [folder], has a
/// `Compile` item outside comments that includes files from outside
/// [folder], as the single-project templates' test projects compile the
/// mod's sources with `<Compile Include="..\..\*.cs" />`.
bool _compilesSourcesFromOutside(String project, String folder) =>
    startTags(project, 'Compile', anyCase: true).any(
      (found) => found.tag.attributes.any((attribute) {
        if (!isAttributeNamed(project, attribute, 'Include')) return false;
        final value = trimmedAttributeValue(project, attribute);
        return project
            .substring(value.start, value.end)
            .split(';')
            .any((spec) => _leavesFolder(spec, folder));
      }),
    );

/// `$(MSBuildProjectDirectory)` and `$(MSBuildThisFileDirectory)` at the
/// start of a path, with the separators after them. Both are reserved
/// properties that name the project file's folder, so a path that starts
/// with one starts there, as a relative path does.
final _projectFolderPrefix = RegExp(
  r'^\$\((?:MSBuildProjectDirectory|MSBuildThisFileDirectory)\)[\\/]*',
  caseSensitive: false,
);

/// Whether [spec], one path of a `Compile` item's `Include`, names files
/// outside [folder], the folder of its project file, once its `..` parts
/// are resolved as MSBuild resolves them, with either separator.
bool _leavesFolder(String spec, String folder) {
  final relative = spec
      .trim()
      .replaceFirst(_projectFolderPrefix, '')
      .replaceAll(r'\', '/');
  final path = p.normalize(p.join(folder, relative));
  return !p.equals(path, folder) && !p.isWithin(folder, path);
}
