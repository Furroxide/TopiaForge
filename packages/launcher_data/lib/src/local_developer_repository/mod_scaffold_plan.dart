part of '../local_developer_repository.dart';

/// What `createModProject` decided before it created anything: every
/// argument checked, the template found and read, and the manifest and the
/// license text built. Creating the project then only writes what the plan
/// holds.
final class _ModScaffoldPlan {
  const _ModScaffoldPlan({
    required this.root,
    required this.id,
    required this.name,
    required this.templateDir,
    required this.tokens,
    required this.manifest,
    required this.licenseText,
    required this.withCompanion,
  });

  /// The project folder, which did not exist when the plan was made.
  final Directory root;
  final String id;
  final String name;

  /// The template folder to copy, or null for the built-in minimal starter,
  /// which stands in when `templates/mod/minimal` is absent (synthetic test
  /// environments).
  final Directory? templateDir;
  final Map<String, String> tokens;
  final ModManifest manifest;
  final String licenseText;
  final bool withCompanion;
}

/// Mod scaffolding in two steps: plan everything, then write it. A refused
/// argument or an unknown template leaves nothing on disk, and a failure
/// while writing removes only what this run created, so a retry never meets
/// "Project already exists" for a project that was never made.
extension LocalDeveloperModScaffoldPlan on LocalDeveloperRepository {
  /// The most Unicode characters a manifest's `displayName` holds.
  static const _maximumModNameCharacters = 128;

  /// Checks every argument of `createModProject`, reads the template and
  /// builds the manifest and the license text, before anything exists.
  Future<_ModScaffoldPlan> _planModScaffold({
    required String parentDirectory,
    required String id,
    required String name,
    required bool includeUnityCompanion,
    required ModScaffoldOptions options,
  }) async {
    if (!ModManifest.isValidId(id)) {
      throw StateError(
        'Mod id "$id" is not valid. Use 2 to 64 letters, numbers, '
        'underscores, dots or dashes, starting with a letter or a number, '
        'under a name of your own, such as sam.hello-mod.',
      );
    }
    if (name.trim().isEmpty) {
      throw StateError('The mod name must not be blank.');
    }
    if (name.runes.length > _maximumModNameCharacters) {
      throw StateError(
        'The mod name must contain at most $_maximumModNameCharacters '
        'Unicode characters.',
      );
    }
    final root = Directory(p.join(parentDirectory, _safeName(id)));
    if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError('Project already exists: ${root.path}');
    }
    final templates = await _listModTemplates();
    final templateDir = Directory(
      p.join(_modTemplatesRoot.path, options.template),
    );
    final hasTemplateDir =
        templateDir.existsSync() &&
        File(p.join(templateDir.path, 'template.json')).existsSync();
    // A listed template is copied from its folder; only `minimal` may fall
    // back to the built-in starter.
    final listed = templates.firstWhere(
      (template) =>
          template.id == options.template &&
          (hasTemplateDir || template.id == 'minimal'),
      orElse: () => throw StateError(
        'Unknown mod template "${options.template}" (available: '
        '${templates.map((template) => template.id).join(', ')}).',
      ),
    );
    options.validateForScaffolding(listed.manifestDefaults);
    final tokens = _modTemplateTokens(id, name);
    final info = hasTemplateDir
        ? await _readModTemplateInfo(templateDir, tokens)
        : null;
    final manifest = ModManifest.fromJson(
      _scaffoldManifestMap(id, name, tokens, info, options),
    );
    return _ModScaffoldPlan(
      root: root,
      id: id,
      name: name,
      templateDir: hasTemplateDir ? templateDir : null,
      tokens: tokens,
      manifest: manifest,
      licenseText: _scaffoldLicenseText(manifest, options.licenseText),
      // The explicit flag, the scaffold option, or the template can each ask
      // for the companion.
      withCompanion:
          includeUnityCompanion ||
          options.includeUnityCompanion ||
          listed.includeUnityCompanion,
    );
  }

  /// The `topiaforge.mod.json` map of a new project: the base manifest, then
  /// the template's defaults, then the author's explicit overrides, which a
  /// ModManifest round-trip gives canonical field names and ordering.
  /// Identity stays non-publishable until the author supplies it; the license
  /// defaults to the project's terms and the author may replace it.
  Map<String, Object?> _scaffoldManifestMap(
    String id,
    String name,
    Map<String, String> tokens,
    ModTemplateInfo? info,
    ModScaffoldOptions options,
  ) {
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
      'supportedGameVersionRange': '>=0.0.2545 <0.0.2600',
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
    return options.applyTo(manifestMap);
  }

  /// Writes what [plan] holds into its root, which [_createProjectFolders]
  /// has just created.
  Future<void> _applyModScaffold(_ModScaffoldPlan plan) async {
    final root = plan.root.path;
    await _writeProject(
      root,
      DeveloperProject(
        schemaVersion: 2,
        id: plan.id,
        name: plan.name,
        unityCompanion: plan.withCompanion
            ? UnityCompanionSettings(
                enabled: true,
                projectPath: p.join(root, 'unity-companion'),
                assetBundleOutputPath: 'assets/AssetBundles',
              )
            : const UnityCompanionSettings(),
      ),
    );
    final templateDir = plan.templateDir;
    if (templateDir != null) {
      await _instantiateModTemplate(templateDir, root, plan.tokens);
    } else {
      await _writeStarterModSources(root, plan.id, plan.name);
    }
    _writeDeveloperTextAtomic(
      File(p.join(root, 'topiaforge.mod.json')),
      _prettyJson(plan.manifest.toJson()),
    );
    _writeDeveloperTextAtomic(
      File(p.join(root, 'LICENSE.md')),
      plan.licenseText,
    );
    if (plan.withCompanion) {
      await _writeUnityCompanionScaffold(root, plan.name);
    }
    final sdk = await _initializeSdkProject(root);
    if (sdk != null) {
      await _ensureSdkPackageReferences(root, sdk);
      await _writeDevProps(root, null, sdk: sdk);
    }
    await _ensureProjectGitignore(root);
  }

  /// Creates [root] and the ancestors it lacks, one at a time from the top
  /// down, and returns them in that order, so that a failure can remove
  /// exactly those ([_removeCreated]). A folder that exists already is never
  /// among them: an ancestor that is a file or a broken link is refused
  /// before anything is created, and a folder that appears while they are
  /// created stops the creation.
  List<Directory> _createProjectFolders(Directory root) {
    final missing = <Directory>[root.absolute];
    var folder = root.absolute.parent;
    while (FileSystemEntity.typeSync(folder.path) !=
        FileSystemEntityType.directory) {
      if (FileSystemEntity.typeSync(folder.path, followLinks: false) !=
              FileSystemEntityType.notFound ||
          folder.parent.path == folder.path) {
        throw StateError('Project parent is not a folder: ${folder.path}');
      }
      missing.add(folder);
      folder = folder.parent;
    }
    final created = <Directory>[];
    try {
      for (final next in missing.reversed) {
        // Creating a folder that is already there succeeds silently, and
        // such a folder is not this run's to remove.
        if (FileSystemEntity.typeSync(next.path, followLinks: false) !=
            FileSystemEntityType.notFound) {
          throw StateError(
            'Project target appeared while scaffolding: ${next.path}',
          );
        }
        next.createSync();
        created.add(next);
      }
    } on Object {
      _removeCreated(created, root);
      rethrow;
    }
    return created;
  }

  /// Removes what [_createProjectFolders] created, deepest first: the
  /// project [root] with everything this run wrote into it, then each
  /// ancestor only while it is empty. A non-recursive delete refuses a folder
  /// that holds anything, so nothing else is ever removed. The failure that
  /// led here stays the error the author sees.
  void _removeCreated(List<Directory> created, Directory root) {
    final rootPath = root.absolute.path;
    for (final folder in created.reversed) {
      try {
        folder.deleteSync(recursive: folder.path == rootPath);
      } on FileSystemException {
        return;
      }
    }
  }
}
