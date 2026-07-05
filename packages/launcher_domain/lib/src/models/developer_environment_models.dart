part of '../models.dart';

class DeveloperDoctorReport {
  const DeveloperDoctorReport({
    required this.projectRoot,
    required this.messages,
    this.hasProject = false,
    this.unityHubPath = '',
    this.unityEditorPath = '',
    this.issues = const [],
  });

  final String projectRoot;
  final List<String> messages;

  /// Whether a developer project (robotopia.project.json) was found —
  /// [projectRoot] alone can't tell, it falls back to the requested path.
  final bool hasProject;
  final String unityHubPath;
  final String unityEditorPath;
  final List<LauncherIssue> issues;

  bool get ok => issues.every((issue) => !issue.isBlocking);
}

/// Outcome of probing one developer tool.
enum ToolStatus { ok, outdated, warning, missing }

/// What a tool is needed for. Consuming mods needs none of these.
enum ToolPurpose { develop, ugcAutomerge, ugcUnity, optional }

/// A single environment check with actionable remediation when not OK.
class ToolCheck {
  const ToolCheck({
    required this.name,
    required this.status,
    required this.purpose,
    this.detail = '',
    this.remediation = '',
    this.url = '',
  });

  final String name;
  final ToolStatus status;
  final ToolPurpose purpose;
  final String detail;
  final String remediation;
  final String url;

  bool get ok => status == ToolStatus.ok;
}

/// Audit of the developer toolchain.
class EnvironmentReport {
  const EnvironmentReport({required this.checks});

  final List<ToolCheck> checks;

  Iterable<ToolCheck> ofPurpose(ToolPurpose purpose) =>
      checks.where((check) => check.purpose == purpose);

  bool get developerReady => ofPurpose(ToolPurpose.develop).every((c) => c.ok);

  bool get ugcAutomergeReady =>
      ofPurpose(ToolPurpose.ugcAutomerge).every((c) => c.ok);

  bool get ugcUnityReady => ofPurpose(ToolPurpose.ugcUnity).every((c) => c.ok);

  List<ToolCheck> get blockers =>
      ofPurpose(ToolPurpose.develop).where((c) => !c.ok).toList();
}

/// Result of `DeveloperRepository.runSetup()`.
class DeveloperSetupResult {
  const DeveloperSetupResult({
    required this.environment,
    this.actions = const [],
    this.issues = const [],
  });

  final EnvironmentReport environment;
  final List<String> actions;
  final List<LauncherIssue> issues;

  bool get ok => environment.developerReady;
}

class LegacyMigrationResult {
  const LegacyMigrationResult({
    required this.outputRoot,
    required this.createdProjects,
    required this.issues,
  });

  final String outputRoot;
  final List<String> createdProjects;
  final List<LauncherIssue> issues;
}

List<PackageSource> _packageSourceList(Object? value) {
  if (value is! List) {
    return const [];
  }
  return value
      .whereType<Map>()
      .map((item) => PackageSource.fromJson(_objectMap(item)))
      .where((item) => item.id.trim().isNotEmpty && item.url.trim().isNotEmpty)
      .toList(growable: false);
}

List<LockedPackage> _lockedPackageList(Object? value) {
  if (value is! List) {
    return const [];
  }
  return value
      .whereType<Map>()
      .map((item) => LockedPackage.fromJson(_objectMap(item)))
      .where((item) => item.id.trim().isNotEmpty)
      .toList(growable: false);
}

Map<String, List<String>> _stringListMap(Object? value) {
  if (value is! Map) {
    return const {};
  }
  return value.map(
    (key, mapValue) => MapEntry(key.toString(), _stringList(mapValue)),
  );
}
