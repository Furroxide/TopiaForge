part of '../local_developer_repository.dart';

/// Directory-template mod scaffolding + manifest management. Templates live at `templates/mod/<id>/` in the repo,
/// each with a `template.json` (metadata + partial-manifest defaults) and tokenized source files. The manifest is
/// always generated programmatically (base → template defaults → CLI overrides → `ModManifest` round-trip) so a
/// fresh scaffold is guaranteed to pass `check package`.
extension LocalDeveloperModScaffolding on LocalDeveloperRepository {
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

    final tokens = _modTemplateTokens(root, id, name);
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
      'schemaVersion': 3,
      'name': id,
      'displayName': name,
      'version': '0.1.0',
      'author': {'name': TopiaForgeScaffoldDefaults.authorName},
      'description': '',
      'entryAssembly': '${tokens['ASSEMBLY_NAME']}.dll',
      'entryType': '${tokens['ASSEMBLY_NAME']}.${tokens['TYPE_NAME']}Mod',
      'vpmDependencies': <String, Object?>{},
      'supportedSdkVersionRange': '>=0.1.0 <0.2.0',
      'apiAssemblies': <Object?>[],
      'license': TopiaForgeScaffoldDefaults.license,
    };
    if (info != null && info.manifestDefaults.isNotEmpty) {
      // The V1 SDK templates use manifest V4 names. Until the launcher V4
      // implementation lands in the following package-devex batch, translate
      // those defaults into the V3 names understood by this repository layer.
      final defaults = Map<String, Object?>.of(info.manifestDefaults);
      final capabilities = defaults.remove('capabilities');
      if (capabilities != null) {
        defaults['permissions'] = capabilities;
      }
      final dependencies = defaults.remove('dependencies');
      if (dependencies != null) {
        defaults['vpmDependencies'] = dependencies;
      }
      manifestMap = {...manifestMap, ...defaults};
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

  Map<String, String> _modTemplateTokens(String root, String id, String name) {
    final assembly = _assemblyName(id);
    return {
      'MOD_ID': id,
      'DISPLAY_NAME': name,
      'ASSEMBLY_NAME': assembly,
      'TYPE_NAME': _typeName(id),
      // The same derivation `topiaforge world link` uses, so a scaffolded world mod and its paired Unity
      // project agree on the bundle name out of the box.
      'BUNDLE_NAME': WorldAuthoringConfig.deriveBundleName(id),
      // V1 template sources are part of the SDK contract in this batch. The
      // package-devex batch replaces this checkout bridge with the generated
      // SDK reference pack and derives the value from its canonical metadata.
      'SDK_VERSION': '1.0.0',
      'ABSTRACTIONS_PROJECT': _canonicalRelativeReference(
        p.join(
          _repositoryRoot.path,
          'src',
          'TopiaForge.Mods.Abstractions',
          'TopiaForge.Mods.Abstractions.csproj',
        ),
        from: root,
      ),
      'UNITYUI_PROJECT': _canonicalRelativeReference(
        p.join(
          _repositoryRoot.path,
          'src',
          'TopiaForge.Mods.UnityUi',
          'TopiaForge.Mods.UnityUi.csproj',
        ),
        from: root,
      ),
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
          var rendered = substitute(content);
          if (p.extension(entity.path).toLowerCase() == '.csproj') {
            rendered = _bridgeSdkPackageReferences(target, rendered);
          }
          await File(target).writeAsString(rendered);
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

  String _bridgeSdkPackageReferences(String projectPath, String project) {
    const sdkProjects = <String, String>{
      'TopiaForge.Mods.Abstractions':
          'src/TopiaForge.Mods.Abstractions/TopiaForge.Mods.Abstractions.csproj',
      'TopiaForge.Mods.Analyzers':
          'src/TopiaForge.Mods.Analyzers/TopiaForge.Mods.Analyzers.csproj',
      'TopiaForge.Mods.RobotKit':
          'src/TopiaForge.Mods.RobotKit/TopiaForge.Mods.RobotKit.csproj',
      'TopiaForge.Mods.Testing':
          'src/TopiaForge.Mods.Testing/TopiaForge.Mods.Testing.csproj',
      'TopiaForge.Mods.Worlds':
          'src/TopiaForge.Mods.Worlds/TopiaForge.Mods.Worlds.csproj',
    };
    final pattern = RegExp(
      r'^[ \t]*<PackageReference Include="([^"]+)" Version="1\.0\.0"(?: PrivateAssets="all")? />[ \t]*$',
      multiLine: true,
    );
    final bridged = project.replaceAllMapped(pattern, (match) {
      final package = match.group(1)!;
      final repositoryPath = sdkProjects[package];
      if (repositoryPath == null) {
        return match.group(0)!;
      }
      final include = _canonicalRelativeReference(
        p.join(_repositoryRoot.path, p.joinAll(p.posix.split(repositoryPath))),
        from: File(projectPath).parent.path,
      );
      if (package == 'TopiaForge.Mods.Analyzers') {
        return '    <ProjectReference Include="$include" '
            'OutputItemType="Analyzer" ReferenceOutputAssembly="false" />';
      }
      return '    <ProjectReference Include="$include" Private="false" />';
    });
    return bridged.replaceFirst(
      RegExp(
        r'\n[ \t]*<Target Name="EnsureTopiaForgeSdk".*?</Target>[ \t]*\n',
        dotAll: true,
      ),
      '\n',
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
