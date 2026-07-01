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
    final dotnet = await _which('dotnet');
    if (dotnet.isEmpty) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: '.NET SDK was not found on PATH.',
        ),
      );
    } else {
      messages.add('.NET SDK found at $dotnet.');
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
    final dotnet = await _which('dotnet');
    if (dotnet.isEmpty) {
      checks.add(
        const ToolCheck(
          name: '.NET SDK',
          status: ToolStatus.missing,
          purpose: ToolPurpose.develop,
          detail: 'Not found on PATH.',
          remediation:
              'Install the .NET SDK 8.0 or newer to build and pack mods.',
          url: 'https://dotnet.microsoft.com/download',
        ),
      );
    } else {
      final version = await _toolVersion('dotnet', const ['--version']);
      final major = _majorVersion(version);
      final outdated = major != null && major < 8;
      checks.add(
        ToolCheck(
          name: '.NET SDK',
          status: outdated ? ToolStatus.outdated : ToolStatus.ok,
          purpose: ToolPurpose.develop,
          detail: version.isEmpty ? dotnet : 'v$version',
          remediation: outdated ? 'Upgrade to the .NET SDK 8.0 or newer.' : '',
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

    // Unity — only needed to author UGC content in the companion.
    final unityEditor = await _findUnityEditor(null);
    final unityHub = await _findUnityHub();
    checks.add(
      ToolCheck(
        name: 'Unity Editor',
        status: unityEditor.isEmpty ? ToolStatus.missing : ToolStatus.ok,
        purpose: ToolPurpose.ugcUnity,
        detail: unityEditor.isNotEmpty
            ? unityEditor
            : (unityHub.isEmpty
                  ? 'Unity not detected (optional).'
                  : 'Hub found, editor not detected: $unityHub'),
        remediation: unityEditor.isEmpty
            ? 'Install Unity via Unity Hub only if you author UGC content in the companion.'
            : '',
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
      final result = await Process.run(executable, args, runInShell: true);
      if (result.exitCode == 0) {
        return (result.stdout as String).trim().split('\n').first.trim();
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
        if (Directory(p.join(sidecarDir, 'node_modules')).existsSync()) {
          actions.add('UGC Automerge sidecar dependencies already present.');
        } else {
          final result = await _installSidecarDeps(sidecarDir);
          actions.add(result.action);
          if (result.issue != null) {
            issues.add(result.issue!);
          }
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
