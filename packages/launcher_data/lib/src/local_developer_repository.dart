import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'agpl_3_0_text.g.dart';
import 'atomic_file_writes.dart';
import 'atomic_files.dart';
import 'bounded_process.dart';
import 'data_root.dart';
import 'dotnet_sdk.dart';
import 'linear_text_scan.dart';
import 'nuget_lock_check.dart';
import 'package_contract.dart';
import 'public_url.dart';
import 'safe_zip_archive.dart';
import 'secure_http.dart';
import 'sdk_package_references.dart';
import 'sdk_reference_pack.dart';

part 'local_developer_repository/io_helpers.dart';
part 'local_developer_repository/template_copy.dart';
part 'local_developer_repository/archive_safety.dart';
part 'local_developer_repository/data_root_repository.dart';
part 'local_developer_repository/pack_helpers.dart';
part 'local_developer_repository/multiplayer_contract_lock.dart';
part 'local_developer_repository/mod_scaffolding.dart';
part 'local_developer_repository/mod_scaffold_plan.dart';
part 'local_developer_repository/license_scaffolding.dart';
part 'local_developer_repository/environment_helpers.dart';
part 'local_developer_repository/project_registry.dart';
part 'local_developer_repository/source_helpers.dart';
part 'local_developer_repository/source_models.dart';
part 'local_developer_repository/package_restore.dart';
part 'local_developer_repository/sdk_restore.dart';
part 'local_developer_repository/sdk_restore_models.dart';
part 'local_developer_repository/restored_sdk_check.dart';
part 'local_developer_repository/unity_vpm.dart';
part 'local_developer_repository/unity_package_scaffolding.dart';
part 'local_developer_repository/unity_vpm_network.dart';
part 'local_developer_repository/vpm_restore.dart';
part 'local_developer_repository/unity_vpm_packaging.dart';
part 'local_developer_repository/world_authoring.dart';
part 'local_developer_repository/world_bundle_attestation.dart';

class LocalDeveloperRepository extends _DeveloperDataRootRepository {
  LocalDeveloperRepository({
    String? dataRoot,
    String? repositoryRoot,
    String? workingDirectory,
    DeveloperProjectResolver resolver = const DeveloperProjectResolver(),
    this.manifestFeatures = ManifestFeatureRequirements.current,
    UnityEditorScanner? unityEditorScanner,
    UnityEditorVersionProbe? unityEditorVersionProbe,
    UnityEditorLauncher? unityEditorLauncher,
    RepositoryDotnetSdkResolver? dotnetSdkResolver,
    ExecutableLocator? executableLocator,
    Duration unityEditorProbeTimeout = const Duration(seconds: 5),
  }) : _repositoryRoot = Directory(
         repositoryRoot ?? _findDeveloperRepoRoot(workingDirectory),
       ),
       _resolver = resolver,
       _unityEditorScanner = unityEditorScanner,
       _unityEditorVersionProbe = unityEditorVersionProbe,
       _unityEditorLauncher = unityEditorLauncher,
       _dotnetSdkResolver = dotnetSdkResolver ?? resolveRepositoryDotnetSdk,
       _executableLocator = executableLocator,
       _unityEditorProbeTimeout = unityEditorProbeTimeout,
       super(Directory(dataRoot ?? resolveTopiaForgeDataRoot()));

  final Directory _repositoryRoot;
  final ManifestFeatureRequirements manifestFeatures;
  final DeveloperProjectResolver _resolver;
  final UnityEditorScanner? _unityEditorScanner;
  final UnityEditorVersionProbe? _unityEditorVersionProbe;
  final UnityEditorLauncher? _unityEditorLauncher;
  final RepositoryDotnetSdkResolver _dotnetSdkResolver;
  final ExecutableLocator? _executableLocator;
  final Duration _unityEditorProbeTimeout;

  @override
  Future<DeveloperWorkspace> loadDeveloperWorkspace({
    String? projectPath,
  }) async {
    final root = _findProjectRoot(projectPath ?? Directory.current.path);
    if (root == null) {
      return DeveloperWorkspace(
        projectRoot: projectPath ?? Directory.current.path,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.warning,
            message: 'topiaforge.project.json was not found.',
          ),
        ],
      );
    }

    final project = await _readProject(root.path);
    final lock = await _readLock(root.path);
    return DeveloperWorkspace(
      projectRoot: root.path,
      project: project,
      lock: lock,
      generatedPropsPath: p.join(root.path, 'topiaforge.dev.props'),
      issues: project.schemaVersion == 2
          ? const []
          : const [
              LauncherIssue(
                severity: IssueSeverity.error,
                message: 'topiaforge.project.json schemaVersion must be 2.',
              ),
            ],
    );
  }

  @override
  Future<DeveloperWorkspace> createModProject({
    required String parentDirectory,
    required String id,
    required String name,
    bool includeUnityCompanion = false,
    ModScaffoldOptions options = const ModScaffoldOptions(),
  }) async {
    // Every argument is checked and the template read before anything
    // exists, and a failure while writing removes only what this run
    // created, so a retry never meets a half-made project.
    final plan = await _planModScaffold(
      parentDirectory: parentDirectory,
      id: id,
      name: name,
      includeUnityCompanion: includeUnityCompanion,
      options: options,
    );
    final created = _createProjectFolders(plan.root);
    try {
      await _applyModScaffold(plan);
    } on Object {
      _removeCreated(created, plan.root);
      rethrow;
    }
    // Registry writes are best-effort; project files stay valid if this fails.
    try {
      await _registerProject(plan.root.path);
    } on Object {
      // ignore: registration is non-essential.
    }
    return loadDeveloperWorkspace(projectPath: plan.root.path);
  }

  @override
  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  }) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    // Resolve is a build step, so its default source set stays local and
    // offline-deterministic. The official registry participates when a
    // project opts in: `topiaforge add source official <url>`.
    final sources = project.packageSources.isEmpty
        ? [_localSource()]
        : project.packageSources;
    final loaded = await _loadRegistryModsGuarded(sources);
    final resolution = _resolver.resolve(
      project,
      loaded.mods,
      includePrerelease: includePrerelease,
    );
    var lock = resolution.lock;
    final restored = <LauncherIssue>[];
    if (restore && !resolution.hasBlockingIssues) {
      final sdk = await _restoreProjectSdk(root.path);
      lock = await _restoreLockedPackages(root.path, lock);
      await _writeDevProps(root.path, lock, sdk: sdk);
      if (sdk != null) {
        await _ensureSdkPackageReferences(root.path, sdk);
        restored.addAll(await _restoreNuGetPackages(root.path, sdk));
      }
      await _ensureProjectGitignore(root.path);
    }
    await _writeLock(root.path, lock);
    return DeveloperWorkspace(
      projectRoot: root.path,
      project: project,
      lock: lock,
      generatedPropsPath: p.join(root.path, 'topiaforge.dev.props'),
      issues: [...loaded.issues, ...resolution.issues, ...restored],
    );
  }

  @override
  Future<DeveloperDoctorReport> runDoctor({String? projectPath}) =>
      _runDoctor(projectPath: projectPath);

  @override
  Future<EnvironmentReport> checkEnvironment() => _checkEnvironment();

  @override
  Future<DeveloperSetupResult> runSetup() => _runSetup();

  @override
  Future<ModManifest> checkPackage(String packagePath) async {
    // Accept both a packed .topiaforgemod archive and an unpacked mod directory (e.g. a fresh scaffold), so
    // authors can validate before ever packing.
    final ModManifest manifest;
    if (FileSystemEntity.isDirectorySync(packagePath)) {
      final file = File(p.join(packagePath, 'topiaforge.mod.json'));
      if (!file.existsSync()) {
        throw StateError('topiaforge.mod.json was not found in $packagePath.');
      }
      manifest = ModManifest.fromJson(
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
    } else {
      manifest = (await _readPackage(packagePath, expectedSha256: '')).manifest;
    }
    final issues = [
      ...manifest.validate(),
      ...manifestFeatures.validate(manifest),
    ];
    if (issues.any((issue) => issue.isBlocking)) {
      throw StateError(issues.map((issue) => issue.message).join(' '));
    }
    return manifest;
  }

  @override
  Future<DeveloperProject> addProjectPackageSource(
    String projectPath,
    PackageSource source,
  ) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    final sources = [
      ...project.packageSources.where(
        (item) => item.id.toLowerCase() != source.id.toLowerCase(),
      ),
      source,
    ];
    final updated = project.copyWith(packageSources: sources);
    await _writeProject(root.path, updated);
    return updated;
  }

  @override
  Future<DeveloperProject> addProjectDependency(
    String projectPath,
    ModDependency dependency,
  ) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    final dependencies = [
      ...project.dependencies.where(
        (item) => item.id.toLowerCase() != dependency.id.toLowerCase(),
      ),
      dependency,
    ];
    final updated = project.copyWith(dependencies: dependencies);
    await _writeProject(root.path, updated);
    return updated;
  }

  @override
  Future<DeveloperProject> removeProjectDependency(
    String projectPath,
    String dependencyId,
  ) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    final updated = project.copyWith(
      dependencies: project.dependencies
          .where((item) => item.id.toLowerCase() != dependencyId.toLowerCase())
          .toList(),
    );
    await _writeProject(root.path, updated);
    return updated;
  }

  @override
  Future<List<ModTemplateInfo>> listModTemplates() => _listModTemplates();

  @override
  Future<ModManifest> readModManifest(String projectPath) =>
      _readModManifest(projectPath);

  @override
  Future<List<LauncherIssue>> updateModManifest(
    String projectPath,
    ModManifest manifest,
  ) => _updateModManifest(projectPath, manifest);

  @override
  Future<String> packProject(
    String projectPath, {
    String outputDir = '',
    String configuration = 'Release',
  }) async {
    final root = _requireProjectRoot(projectPath);
    final sdk = await _restoreProjectSdk(root.path);
    await _writeDevProps(root.path, await _readLock(root.path), sdk: sdk);
    if (sdk != null) {
      await _ensureSdkPackageReferences(root.path, sdk);
      // A package built against another SDK version than the pin is never
      // packed.
      final restored = await _restoreNuGetPackages(root.path, sdk);
      if (restored.isNotEmpty) {
        throw StateError(restored.map((issue) => issue.message).join('\n'));
      }
    }
    return _packModProject(
      root,
      outputDir: outputDir,
      configuration: configuration,
    );
  }

  @override
  Future<List<RegisteredProject>> listProjects() => _readRegistry();

  @override
  Future<List<RegisteredProject>> addExistingProject(String path) =>
      _registerProject(path);

  @override
  Future<List<RegisteredProject>> removeProject(String path) =>
      _unregisterProject(path);

  @override
  Future<List<RegisteredProject>> createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  }) => _createUnityProject(
    parentDirectory: parentDirectory,
    name: name,
    template: template,
  );

  @override
  Future<List<RegisteredProject>> touchProjectOpened(String path) =>
      _touchProject(path);

  @override
  Future<List<UnityEditor>> listUnityEditors() => _scanUnityEditors();

  @override
  Future<String> openProjectInUnity(String projectPath) =>
      _openInUnity(projectPath);

  @override
  Future<List<VpmResolvedPackage>> resolveUnityProject(
    String projectPath, {
    bool restore = true,
  }) => _resolveUnityProject(projectPath, restore: restore);

  @override
  Future<List<VpmResolvedPackage>> addUnityPackage(
    String projectPath,
    String id,
    String versionRange,
  ) => _addUnityPackage(projectPath, id, versionRange);

  @override
  Future<List<VpmResolvedPackage>> removeUnityPackage(
    String projectPath,
    String id,
  ) => _removeUnityPackage(projectPath, id);

  @override
  Future<List<VpmPackageInfo>> listAvailableUnityPackages() =>
      _listAvailableUnityPackages();

  @override
  Future<List<PackageSource>> listUnityRepos() => _loadVpmSources();

  @override
  Future<List<PackageSource>> addUnityRepo(String url, {String name = ''}) =>
      _addVpmSource(url, name);

  @override
  Future<List<PackageSource>> removeUnityRepo(String id) =>
      _removeVpmSource(id);

  @override
  Future<String> createUnityPackage({
    required String parentDirectory,
    required String id,
    String name = '',
  }) => _createUnityPackage(parentDirectory, id, name);

  @override
  Future<WorldAuthoringConfig?> readWorldAuthoringConfig(
    String unityProjectPath,
  ) => _readWorldAuthoringConfig(unityProjectPath);

  @override
  Future<WorldAuthoringConfig> writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  ) => _writeWorldAuthoringConfig(unityProjectPath, config);

  @override
  Future<WorldBundleBuildResult> buildWorldBundle({
    required String unityProjectPath,
    String modPath = '',
    String bundleName = '',
    String unityExePath = '',
  }) => _buildWorldBundle(
    unityProjectPath: unityProjectPath,
    modPath: modPath,
    bundleName: bundleName,
    unityExePath: unityExePath,
  );
}
