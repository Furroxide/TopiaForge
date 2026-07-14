part of '../local_developer_repository.dart';

extension LocalDeveloperEnvironmentOperations on LocalDeveloperRepository {
  Future<DeveloperDoctorReport> _runDoctor({String? projectPath}) async {
    final workspace = await loadDeveloperWorkspace(projectPath: projectPath);
    final messages = <String>[];
    final issues = <LauncherIssue>[...workspace.issues];
    messages.add(
      workspace.hasProject
          ? 'Developer project found at ${workspace.projectRoot}.'
          : 'Developer project not found.',
    );
    try {
      final dotnet = await _dotnetSdkResolver(_repositoryRoot);
      messages.add('.NET SDK ${dotnet.version} found at ${dotnet.executable}.');
    } on Object catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message: _dotnetDiscoveryMessage(error),
        ),
      );
    }

    final unityHub = await _findUnityHub();
    final unityEditor = await _findUnityEditor(workspace.project);
    messages.add(
      unityHub.isEmpty ? 'Unity Hub not detected.' : 'Unity Hub: $unityHub',
    );
    messages.add(
      unityEditor.isEmpty
          ? 'Unity Editor not detected.'
          : 'Unity Editor: $unityEditor',
    );

    if (workspace.project?.unityCompanion.enabled == true) {
      _checkUgcCompanion(workspace, messages, issues);
    }

    return DeveloperDoctorReport(
      projectRoot: workspace.projectRoot,
      messages: messages,
      hasProject: workspace.hasProject,
      unityHubPath: unityHub,
      unityEditorPath: unityEditor,
      issues: issues,
    );
  }

  // Verifies the UGC live-sync dev loop is wired up: the companion Unity package is present, and the configured
  // watch folder can actually be written to (the Unity exporter and the game both need that folder).
  void _checkUgcCompanion(
    DeveloperWorkspace workspace,
    List<String> messages,
    List<LauncherIssue> issues,
  ) {
    final packageJson = File(
      p.join(
        workspace.projectRoot,
        'unity-companion',
        'Packages',
        'com.robotopia.ugc-companion',
        'package.json',
      ),
    );
    if (packageJson.existsSync()) {
      messages.add('UGC companion package present.');
    } else {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.warning,
          message:
              'UGC companion package missing. Re-scaffold with '
              '`robotopia new mod --unity-companion` or copy '
              'unity-companion/Packages/com.robotopia.ugc-companion into the project.',
        ),
      );
    }

    final watchFolder =
        workspace.project?.unityCompanion.liveSync.watchFolder ?? '';
    if (watchFolder.isEmpty) {
      messages.add(
        'UGC watch folder is not set. Set it in the in-game UGC Live Sync panel '
        'or the launcher developer view.',
      );
      return;
    }

    try {
      final dir = Directory(watchFolder)..createSync(recursive: true);
      final probe = File(p.join(dir.path, '.robotopia-doctor-probe'));
      probe.writeAsStringSync('ok');
      probe.deleteSync();
      messages.add('UGC watch folder is writable: $watchFolder');
    } on Object catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.warning,
          message: 'UGC watch folder is not writable: $watchFolder ($error)',
        ),
      );
    }
  }

  Future<EnvironmentReport> _checkEnvironment() async {
    final checks = <ToolCheck>[];

    // .NET SDK — required to build and pack mods.
    try {
      final dotnet = await _dotnetSdkResolver(_repositoryRoot);
      final major = _majorVersion(dotnet.version);
      final outdated = major != null && major < 8;
      checks.add(
        ToolCheck(
          name: '.NET SDK',
          status: outdated ? ToolStatus.outdated : ToolStatus.ok,
          purpose: ToolPurpose.develop,
          detail: 'v${dotnet.version} (${dotnet.executable})',
          remediation: outdated ? 'Upgrade to the .NET SDK 8.0 or newer.' : '',
          url: 'https://dotnet.microsoft.com/download',
        ),
      );
    } on Object catch (error) {
      checks.add(
        ToolCheck(
          name: '.NET SDK',
          status: ToolStatus.missing,
          purpose: ToolPurpose.develop,
          detail: _dotnetDiscoveryMessage(error),
          remediation:
              'Install the exact SDK pinned by global.json or set '
              'ROBOTOPIA_DOTNET_PATH to a compatible dotnet executable.',
          url: 'https://dotnet.microsoft.com/download',
        ),
      );
    }

    // Node.js — only needed for the optional UGC Automerge live-sync channel.
    final node = await _which('node');
    if (node.isEmpty) {
      checks.add(
        const ToolCheck(
          name: 'Node.js',
          status: ToolStatus.missing,
          purpose: ToolPurpose.ugcAutomerge,
          detail: 'Not found (optional).',
          remediation:
              'Install Node.js 20+ only if you publish via the UGC Automerge live-sync channel.',
          url: 'https://nodejs.org/',
        ),
      );
    } else {
      final version = await _toolVersion('node', const ['--version']);
      final major = _majorVersion(version);
      final outdated = major != null && major < 20;
      checks.add(
        ToolCheck(
          name: 'Node.js',
          status: outdated ? ToolStatus.outdated : ToolStatus.ok,
          purpose: ToolPurpose.ugcAutomerge,
          detail: version.isEmpty ? node : version,
          remediation: outdated
              ? 'Upgrade to Node.js 20+ for the Automerge sidecar.'
              : '',
          url: 'https://nodejs.org/',
        ),
      );
    }

    // Unity — only needed to author UGC content in the companion or build custom-world bundles.
    final unityHub = await _findUnityHub();
    // World/UI bundle builds must use the exact game-player editor.
    final editors = await _scanUnityEditors();
    final unityEditor = RobotopiaUnityCompatibility.selectEditor(editors);
    final ToolStatus unityStatus;
    final String unityDetail;
    final String unityRemediation;
    if (editors.isEmpty) {
      unityStatus = ToolStatus.missing;
      unityDetail = unityHub.isEmpty
          ? 'Unity not detected (optional).'
          : 'Hub found, editor not detected: $unityHub';
      unityRemediation =
          'Install Unity via Unity Hub only if you author UGC content or custom worlds.';
    } else if (unityEditor == null) {
      unityStatus = ToolStatus.warning;
      unityDetail =
          'Found ${editors.map((editor) => editor.version).join(', ')} — none can build '
          'world/UI bundles (needs Unity '
          '${RobotopiaUnityCompatibility.requiredEditorDisplay}).';
      unityRemediation = WorldBundleEditorGate.installHint;
    } else {
      unityStatus = ToolStatus.ok;
      unityDetail = unityEditor.path;
      unityRemediation = '';
    }
    checks.add(
      ToolCheck(
        name: 'Unity Editor',
        status: unityStatus,
        purpose: ToolPurpose.ugcUnity,
        detail: unityDetail,
        remediation: unityRemediation,
        url: 'https://unity.com/download',
      ),
    );

    // Git — optional but recommended for version control.
    final git = await _which('git');
    checks.add(
      ToolCheck(
        name: 'Git',
        status: git.isEmpty ? ToolStatus.warning : ToolStatus.ok,
        purpose: ToolPurpose.optional,
        detail: git.isEmpty ? 'Not found (recommended).' : git,
        remediation: git.isEmpty ? 'Install Git for version control.' : '',
        url: 'https://git-scm.com/downloads',
      ),
    );

    return EnvironmentReport(checks: checks);
  }

  Future<String> _toolVersion(String executable, List<String> args) async {
    try {
      final result = await runBoundedProcess(
        executable,
        args,
        timeout: const Duration(seconds: 10),
      );
      if (result.exitCode == 0) {
        return result.stdout.trim().split('\n').first.trim();
      }
    } on Object {
      // Probe is best-effort; absence is reported by the caller via _which.
    }
    return '';
  }

  int? _majorVersion(String version) {
    final match = RegExp(r'(\d+)').firstMatch(version.replaceFirst('v', ''));
    return match == null ? null : int.tryParse(match.group(1)!);
  }

  String _dotnetDiscoveryMessage(Object error) {
    if (error case StateError(:final message)) return message.toString();
    return 'The .NET SDK could not be validated (${error.runtimeType}).';
  }

  Future<({String action, LauncherIssue? issue})> _installSidecarDeps(
    String sidecarDir,
  ) async {
    try {
      final sidecar = TrustedUgcSidecar.inspectDirectory(Directory(sidecarDir));
      await sidecar.ensureDependencies();
      return (
        action: 'Verified lockfile-backed UGC sidecar dependencies.',
        issue: null,
      );
    } on Object catch (error) {
      return (
        action: 'Could not run npm.',
        issue: LauncherIssue(
          severity: IssueSeverity.warning,
          message:
              'Could not complete npm ci (${error.runtimeType}). '
              'Install Node.js 20+ and retry.',
        ),
      );
    }
  }

  Future<DeveloperSetupResult> _runSetup() async {
    final actions = <String>[];
    final issues = <LauncherIssue>[];

    // Ensure the developer data root exists (where sample projects are scaffolded).
    try {
      _dataRoot.createSync(recursive: true);
    } on Object catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.warning,
          message: 'Could not create the developer data folder: $error',
        ),
      );
    }

    var environment = await checkEnvironment();

    // The only safe auto-fix that needs a tool: install the UGC Automerge sidecar's npm deps when Node is present.
    if (environment.ugcAutomergeReady) {
      final sidecar = _findSidecar();
      if (sidecar == null) {
        actions.add(
          'UGC Automerge sidecar not found; skipped dependency install.',
        );
      } else {
        final sidecarDir = File(sidecar).parent.path;
        final result = await _installSidecarDeps(sidecarDir);
        actions.add(result.action);
        if (result.issue != null) {
          issues.add(result.issue!);
        }
      }
    } else {
      actions.add(
        'Node.js not available; skipped the UGC Automerge sidecar (optional).',
      );
    }

    // Re-check so the returned environment reflects any fixes.
    environment = await checkEnvironment();
    return DeveloperSetupResult(
      environment: environment,
      actions: actions,
      issues: issues,
    );
  }
}
