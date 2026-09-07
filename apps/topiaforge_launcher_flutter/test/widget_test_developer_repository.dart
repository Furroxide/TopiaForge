part of 'widget_test.dart';

class _FakeDeveloperRepository implements DeveloperRepository {
  bool hasProject = true;

  @override
  String get developerDataRoot => '/tmp/topiaforge-developer';
  @override
  Future<DeveloperWorkspace> loadDeveloperWorkspace({String? projectPath}) {
    return Future.value(_workspace());
  }

  @override
  Future<DeveloperWorkspace> createModProject({
    required String parentDirectory,
    required String id,
    required String name,
    bool includeUnityCompanion = false,
    ModScaffoldOptions options = const ModScaffoldOptions(),
  }) {
    return Future.value(_workspace());
  }

  @override
  Future<List<ModTemplateInfo>> listModTemplates() {
    return Future.value(const [ModTemplateInfo(id: 'minimal')]);
  }

  @override
  Future<ModManifest> readModManifest(String projectPath) {
    return Future.value(
      const ModManifest(
        schemaVersion: 5,
        id: 'sample.mod',
        name: 'Sample Mod',
        version: '0.1.0',
      ),
    );
  }

  @override
  Future<List<LauncherIssue>> updateModManifest(
    String projectPath,
    ModManifest manifest,
  ) {
    return Future.value(const <LauncherIssue>[]);
  }

  @override
  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  }) {
    return Future.value(_workspace());
  }

  @override
  Future<DeveloperDoctorReport> runDoctor({String? projectPath}) {
    return Future.value(
      const DeveloperDoctorReport(
        projectRoot: '/tmp/creator',
        messages: ['Developer project found.'],
      ),
    );
  }

  int runSetupCount = 0;
  @override
  Future<EnvironmentReport> checkEnvironment() {
    return Future.value(
      const EnvironmentReport(
        checks: [
          ToolCheck(
            name: '.NET SDK',
            status: ToolStatus.ok,
            purpose: ToolPurpose.develop,
            detail: 'v8.0.100',
          ),
        ],
      ),
    );
  }

  @override
  Future<DeveloperSetupResult> runSetup() async {
    runSetupCount += 1;
    return DeveloperSetupResult(
      environment: await checkEnvironment(),
      actions: const ['Ensured the developer data folder.'],
    );
  }

  @override
  Future<ModManifest> checkPackage(String packagePath) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> addProjectPackageSource(
    String projectPath,
    PackageSource source,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> addProjectDependency(
    String projectPath,
    ModDependency dependency,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> removeProjectDependency(
    String projectPath,
    String dependencyId,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<String> packProject(
    String projectPath, {
    String outputDir = '',
    String configuration = 'Release',
  }) {
    throw UnimplementedError();
  }

  @override
  Future<List<RegisteredProject>> listProjects() async => const [];
  @override
  Future<List<RegisteredProject>> addExistingProject(String path) async =>
      const [];
  @override
  Future<List<RegisteredProject>> removeProject(String path) async => const [];
  @override
  Future<List<RegisteredProject>> createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  }) async => const [];
  @override
  Future<List<RegisteredProject>> touchProjectOpened(String path) async =>
      const [];
  @override
  Future<List<UnityEditor>> listUnityEditors() async => const [];
  @override
  Future<String> openProjectInUnity(String projectPath) async => '';
  @override
  Future<List<VpmResolvedPackage>> resolveUnityProject(
    String projectPath, {
    bool restore = true,
  }) async => const [];
  @override
  Future<List<VpmResolvedPackage>> addUnityPackage(
    String projectPath,
    String id,
    String versionRange,
  ) async => const [];
  @override
  Future<List<VpmResolvedPackage>> removeUnityPackage(
    String projectPath,
    String id,
  ) async => const [];
  @override
  Future<List<VpmPackageInfo>> listAvailableUnityPackages() async => const [];
  @override
  Future<List<PackageSource>> listUnityRepos() async => const [];
  @override
  Future<List<PackageSource>> addUnityRepo(
    String url, {
    String name = '',
  }) async => const [];
  @override
  Future<List<PackageSource>> removeUnityRepo(String id) async => const [];
  @override
  Future<String> createUnityPackage({
    required String parentDirectory,
    required String id,
    String name = '',
  }) async => '';
  @override
  Future<WorldAuthoringConfig?> readWorldAuthoringConfig(
    String unityProjectPath,
  ) async => null;
  @override
  Future<WorldAuthoringConfig> writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  ) async => config;
  @override
  Future<WorldBundleBuildResult> buildWorldBundle({
    required String unityProjectPath,
    String modPath = '',
    String bundleName = '',
    String unityExePath = '',
  }) async => const WorldBundleBuildResult(success: false);
}
