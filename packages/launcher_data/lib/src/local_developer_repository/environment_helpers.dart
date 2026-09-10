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

    final hubProbe = await _probeOptionalTool('Unity Hub', _findUnityHub);
    final editorProbe = await _probeOptionalTool(
      'Unity Editor',
      () => _findUnityEditor(workspace.project),
    );
    for (final (name, probe) in [
      ('Unity Hub', hubProbe),
      ('Unity Editor', editorProbe),
    ]) {
      if (probe.warning.isNotEmpty) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.warning,
            message: '${probe.warning} $_optionalToolRetry',
          ),
        );
      } else {
        final path = probe.value!;
        messages.add(path.isEmpty ? '$name not detected.' : '$name: $path');
      }
    }

    return DeveloperDoctorReport(
      projectRoot: workspace.projectRoot,
      messages: messages,
      hasProject: workspace.hasProject,
      unityHubPath: hubProbe.value ?? '',
      unityEditorPath: editorProbe.value ?? '',
      issues: issues,
    );
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
              'TOPIAFORGE_DOTNET_PATH to a compatible dotnet executable.',
          url: 'https://dotnet.microsoft.com/download',
        ),
      );
    }

    // Unity — only needed to build custom-world AssetBundles.
    final hubProbe = await _probeOptionalTool('Unity Hub', _findUnityHub);
    final unityHub = hubProbe.value ?? '';
    if (hubProbe.warning.isNotEmpty) {
      checks.add(
        ToolCheck(
          name: 'Unity Hub',
          status: ToolStatus.warning,
          purpose: ToolPurpose.optional,
          detail: hubProbe.warning,
          remediation: _optionalToolRetry,
        ),
      );
    }
    // World/UI bundle builds must use the exact game-player editor.
    final editorProbe = await _probeOptionalTool(
      'Unity Editor',
      _scanUnityEditors,
    );
    final editors = editorProbe.value ?? const <UnityEditor>[];
    final unityEditor = RobotopiaGameUnityCompatibility.selectEditor(editors);
    final ToolStatus unityStatus;
    final String unityDetail;
    final String unityRemediation;
    if (editorProbe.warning.isNotEmpty) {
      unityStatus = ToolStatus.warning;
      unityDetail = editorProbe.warning;
      unityRemediation = _optionalToolRetry;
    } else if (editors.isEmpty) {
      unityStatus = ToolStatus.missing;
      unityDetail = unityHub.isEmpty
          ? 'Unity not detected (optional).'
          : 'Hub found, editor not detected: $unityHub';
      unityRemediation =
          'Install Unity via Unity Hub only if you author custom worlds.';
    } else if (unityEditor == null) {
      unityStatus = ToolStatus.warning;
      unityDetail =
          'Found ${editors.map((editor) => editor.version).join(', ')} — none can build '
          'world/UI bundles (needs Unity '
          '${RobotopiaGameUnityCompatibility.requiredEditorDisplay}).';
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
        purpose: ToolPurpose.customWorldUnity,
        detail: unityDetail,
        remediation: unityRemediation,
        url: 'https://unity.com/download',
      ),
    );

    // Git — optional but recommended for version control.
    final gitProbe = await _probeOptionalTool('Git', () => _which('git'));
    final git = gitProbe.value ?? '';
    checks.add(
      ToolCheck(
        name: 'Git',
        status: git.isEmpty ? ToolStatus.warning : ToolStatus.ok,
        purpose: ToolPurpose.optional,
        detail: gitProbe.warning.isNotEmpty
            ? gitProbe.warning
            : git.isEmpty
            ? 'Not found (recommended).'
            : git,
        remediation: gitProbe.warning.isNotEmpty
            ? _optionalToolRetry
            : git.isEmpty
            ? 'Install Git for version control.'
            : '',
        url: 'https://git-scm.com/downloads',
      ),
    );

    return EnvironmentReport(checks: checks);
  }

  int? _majorVersion(String version) {
    final match = RegExp(r'(\d+)').firstMatch(version.replaceFirst('v', ''));
    return match == null ? null : int.tryParse(match.group(1)!);
  }

  String _dotnetDiscoveryMessage(Object error) {
    if (error case StateError(:final message)) return message.toString();
    return 'The .NET SDK could not be validated (${error.runtimeType}).';
  }

  Future<DeveloperSetupResult> _runSetup() async {
    final actions = <String>[];
    final issues = <LauncherIssue>[];

    // Ensure the developer data root exists (where sample projects are scaffolded).
    try {
      _dataRoot.createSync(recursive: true);
      actions.add('Ensured the developer data folder.');
    } on Object catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.warning,
          message: 'Could not create the developer data folder: $error',
        ),
      );
    }

    // Creating the data folder cannot change any probe result, so one pass is enough.
    final environment = await checkEnvironment();
    return DeveloperSetupResult(
      environment: environment,
      actions: actions,
      issues: issues,
    );
  }
}

const _optionalToolRetry =
    'Check tool discovery permissions and PATH, then retry the diagnostic.';

class _OptionalToolProbe<T> {
  const _OptionalToolProbe({this.value, this.warning = ''});

  final T? value;
  final String warning;
}

// Failure containment belongs only to reports. Required editor selection and
// world builds still use the original discovery methods and their error paths.
Future<_OptionalToolProbe<T>> _probeOptionalTool<T>(
  String name,
  Future<T> Function() probe,
) async {
  try {
    return _OptionalToolProbe(value: await probe());
  } on Exception catch (error) {
    final reason = switch (error) {
      BoundedProcessException(failure: BoundedProcessFailure.timeout) =>
        'the discovery process timed out',
      BoundedProcessException(
        failure: BoundedProcessFailure.outputReadFailed,
      ) =>
        'the discovery process output could not be read',
      BoundedProcessException(
        failure: BoundedProcessFailure.terminationFailed,
      ) =>
        'the discovery process exceeded a bound and could not be terminated',
      BoundedProcessException() =>
        'the discovery process exceeded an output bound',
      ProcessException() => 'the discovery process could not be started',
      FileSystemException() => 'installed-tool locations could not be read',
      TimeoutException() => 'the discovery probe timed out',
      _ => 'the discovery probe failed (${error.runtimeType})',
    };
    // Process output and exception messages can contain private host details.
    return _OptionalToolProbe(
      warning: '$name availability is unknown: $reason.',
    );
  }
}
