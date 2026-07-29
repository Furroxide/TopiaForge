part of '../models.dart';

/// Overall result of processing the launcher package inbox.
enum PackageInboxInstallStatus {
  /// Every discovered package was handled successfully, including an empty
  /// inbox.
  success,

  /// At least one mod installed, but another candidate was rejected or could
  /// not be consumed.
  partial,

  /// Candidates were present (or enumeration failed), but no mod installed.
  failure,
}

/// Structured, non-throwing outcome from one package-inbox processing pass.
class PackageInboxInstallOutcome {
  PackageInboxInstallOutcome({
    required this.candidateCount,
    required this.installedCount,
    required this.supersededCount,
    required this.consumedCount,
    required this.invalidCount,
    required this.installFailureCount,
    required this.consumptionFailureCount,
    List<LauncherIssue> issues = const [],
  }) : issues = List.unmodifiable(issues);

  /// Number of `.topiaforgemod` files discovered before preflight.
  final int candidateCount;

  /// Number of selected root packages installed successfully.
  final int installedCount;

  /// Number of valid candidates skipped because another version or path won.
  final int supersededCount;

  /// Number of successful or superseded files removed from the inbox pattern.
  final int consumedCount;

  /// Number of candidates rejected during safe preflight.
  final int invalidCount;

  /// Number of selected candidates whose atomic install failed.
  final int installFailureCount;

  /// Number of installed or superseded candidates that remained processable.
  final int consumptionFailureCount;

  /// Actionable issues retained for launcher presentation and diagnostics.
  final List<LauncherIssue> issues;

  /// Number of discovered files still available for retry or inspection.
  int get retainedCount {
    final retained = candidateCount - consumedCount;
    return retained < 0 ? 0 : retained;
  }

  PackageInboxInstallStatus get status {
    if (issues.isEmpty) return PackageInboxInstallStatus.success;
    if (installedCount > 0) return PackageInboxInstallStatus.partial;
    return PackageInboxInstallStatus.failure;
  }

  IssueSeverity get severity => switch (status) {
    PackageInboxInstallStatus.success => IssueSeverity.info,
    PackageInboxInstallStatus.partial => IssueSeverity.warning,
    PackageInboxInstallStatus.failure => IssueSeverity.error,
  };
}
