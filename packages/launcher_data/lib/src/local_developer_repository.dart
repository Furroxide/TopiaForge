import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

part 'local_developer_repository/io_helpers.dart';
part 'local_developer_repository/environment_helpers.dart';
part 'local_developer_repository/project_registry.dart';
part 'local_developer_repository/source_helpers.dart';
part 'local_developer_repository/unity_vpm.dart';

class LocalDeveloperRepository implements DeveloperRepository {
  LocalDeveloperRepository({
    String? dataRoot,
    String? repositoryRoot,
    DeveloperProjectResolver resolver = const DeveloperProjectResolver(),
  }) : _dataRoot = Directory(dataRoot ?? _defaultDeveloperDataRoot()),
       _repositoryRoot = Directory(repositoryRoot ?? _findDeveloperRepoRoot()),
       _resolver = resolver;

  final Directory _dataRoot;
  final Directory _repositoryRoot;
  final DeveloperProjectResolver _resolver;

  @override
  String get developerDataRoot => _dataRoot.path;

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
            message: 'robotopia.project.json was not found.',
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
      generatedPropsPath: p.join(root.path, 'robotopia.dev.props'),
      issues: project.schemaVersion == 1
          ? const []
          : const [
              LauncherIssue(
                severity: IssueSeverity.error,
                message: 'robotopia.project.json schemaVersion must be 1.',
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
  }) async {
    final safeName = _safeName(id);
    final root = Directory(p.join(parentDirectory, safeName));
    if (root.existsSync()) {
      throw StateError('Project already exists: ${root.path}');
    }
    root.createSync(recursive: true);

    final project = DeveloperProject(
      schemaVersion: 1,
      id: id,
      name: name,
      unityCompanion: includeUnityCompanion
          ? UnityCompanionSettings(
              enabled: true,
              projectPath: p.join(root.path, 'unity-companion'),
              assetBundleOutputPath: 'assets/AssetBundles',
            )
          : const UnityCompanionSettings(),
    );
    await _writeProject(root.path, project);
    await _writeStarterMod(root.path, id, name, includeUnityCompanion);
    await _ensureProjectGitignore(root.path);
    // Track the new project in the VCC-style registry (best-effort: a registry write failure must not fail the
    // creation — the project files are valid and can be re-added later).
    try {
      await _registerProject(root.path);
    } on Object {
      // ignore — registration is non-essential to a successfully scaffolded project.
    }
    return loadDeveloperWorkspace(projectPath: root.path);
  }

  @override
  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  }) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    final sources = project.packageSources.isEmpty
        ? [_localSource()]
        : project.packageSources;
    final registryMods = await _loadRegistryMods(sources);
    final resolution = _resolver.resolve(
      project,
      registryMods,
      includePrerelease: includePrerelease,
    );
    var lock = resolution.lock;
    if (restore && !resolution.hasBlockingIssues) {
      lock = await _restoreLockedPackages(root.path, lock);
      await _writeDevProps(root.path, lock);
      await _ensureProjectGitignore(root.path);
    }
    await _writeLock(root.path, lock);
    return DeveloperWorkspace(
      projectRoot: root.path,
      project: project,
      lock: lock,
      generatedPropsPath: p.join(root.path, 'robotopia.dev.props'),
      issues: resolution.issues,
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
  Future<LegacyMigrationResult> migrateLegacyMods(
    String gamePath,
    String outputRoot,
  ) async {
    final legacyRoot = Directory(p.join(gamePath, 'Mods'));
    final created = <String>[];
    final issues = <LauncherIssue>[];
    Directory(outputRoot).createSync(recursive: true);
    if (!legacyRoot.existsSync()) {
      return LegacyMigrationResult(
        outputRoot: outputRoot,
        createdProjects: created,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.warning,
            message: 'Robotopia/Mods folder was not found.',
          ),
        ],
      );
    }

    for (final entity in legacyRoot.listSync()) {
      if (entity is File && entity.path.toLowerCase().endsWith('.dll')) {
        created.add(await _migrateLegacyDll(entity, outputRoot));
      } else if (entity is Directory) {
        final manifest = File(p.join(entity.path, 'robotopia.mod.json'));
        if (manifest.existsSync()) {
          created.add(await _migrateLegacyFolder(entity, outputRoot));
        } else {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.warning,
              message:
                  '${p.basename(entity.path)} has no robotopia.mod.json and needs manual migration.',
            ),
          );
        }
      }
    }
    return LegacyMigrationResult(
      outputRoot: outputRoot,
      createdProjects: created,
      issues: issues,
    );
  }

  @override
  Future<ModManifest> checkPackage(String packagePath) async {
    final result = await _readPackage(packagePath, expectedSha256: '');
    final issues = result.manifest.validate();
    if (issues.any((issue) => issue.isBlocking)) {
      throw StateError(issues.map((issue) => issue.message).join(' '));
    }
    return result.manifest;
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
  Future<DeveloperProject> updateUgcLiveSync(
    String projectPath,
    UgcLiveSyncSettings settings,
  ) async {
    final root = _requireProjectRoot(projectPath);
    final project = await _readProject(root.path);
    final updated = project.withUgcLiveSync(settings);
    await _writeProject(root.path, updated);
    return updated;
  }

  @override
  Future<String> packProject(
    String projectPath, {
    String outputDir = '',
    String configuration = 'Release',
  }) async {
    final root = _requireProjectRoot(projectPath);
    final script = p.join(_repositoryRoot.path, 'tools', 'pack-mod.ps1');
    if (!File(script).existsSync()) {
      throw StateError('pack-mod.ps1 was not found at $script.');
    }
    final args = [
      '-NoProfile',
      '-ExecutionPolicy',
      'Bypass',
      '-File',
      script,
      '-ProjectDir',
      root.path,
      '-Configuration',
      configuration,
      if (outputDir.isNotEmpty) ...['-OutputDir', outputDir],
    ];
    final result = await Process.run('powershell.exe', args);
    if (result.exitCode != 0) {
      throw StateError('${result.stdout}\n${result.stderr}'.trim());
    }
    return result.stdout.toString().trim().split('\n').last.trim();
  }

  // ---- VCC-style multi-project registry + Unity detect/open (helpers in project_registry.dart) ----

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

  // ---- Unity-side VPM (VPM-compatible resolver + listings; helpers in unity_vpm.dart) ----

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
}
