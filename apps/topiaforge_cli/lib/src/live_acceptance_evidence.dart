import 'dart:convert';

import 'live_acceptance_models.dart';

/// Serializable evidence emitted for both successful and failed live runs.
final class LiveAcceptanceEvidence {
  const LiveAcceptanceEvidence({
    required this.startedAtUtc,
    required this.completedAtUtc,
    required this.gameDirectory,
    required this.packagePath,
    required this.requiredCases,
    required this.passedCases,
    required this.missingCases,
    required this.failures,
    required this.lastRunSessionId,
    required this.acceptancePackageStatus,
    required this.releaseJourneyEnabled,
    required this.releaseJourneyCli,
    required this.releaseJourneyProject,
    required this.requiredLoadedPackageId,
    required this.requiredLoadedPackageStatus,
    required this.requiredLogMarker,
    required this.requiredLogMarkerObserved,
    required this.succeeded,
  });

  final DateTime startedAtUtc;
  final DateTime completedAtUtc;
  final String gameDirectory;
  final String packagePath;
  final List<String> requiredCases;
  final List<String> passedCases;
  final List<String> missingCases;
  final List<String> failures;
  final String lastRunSessionId;
  final String acceptancePackageStatus;
  final bool releaseJourneyEnabled;
  final String releaseJourneyCli;
  final String releaseJourneyProject;
  final String requiredLoadedPackageId;
  final String requiredLoadedPackageStatus;
  final String requiredLogMarker;
  final bool requiredLogMarkerObserved;
  final bool succeeded;

  Map<String, Object?> toJson() => {
    'schemaVersion': 1,
    'startedAtUtc': startedAtUtc.toUtc().toIso8601String(),
    'completedAtUtc': completedAtUtc.toUtc().toIso8601String(),
    'gameDirectory': gameDirectory,
    'package': packagePath,
    'requiredCases': requiredCases,
    'passedCases': passedCases,
    'missingCases': missingCases,
    'failures': failures,
    'lastRunSessionId': lastRunSessionId,
    'acceptancePackageStatus': acceptancePackageStatus,
    'releaseJourneyEnabled': releaseJourneyEnabled,
    'releaseJourneyAuthoringCommandCount': releaseJourneyEnabled ? 2 : 0,
    'releaseJourneyCli': releaseJourneyEnabled ? releaseJourneyCli : '',
    'releaseJourneyProject': releaseJourneyEnabled ? releaseJourneyProject : '',
    'requiredLoadedPackageId': releaseJourneyEnabled
        ? requiredLoadedPackageId
        : '',
    'requiredLoadedPackageStatus': requiredLoadedPackageStatus,
    'requiredLogMarker': releaseJourneyEnabled ? requiredLogMarker : '',
    'requiredLogMarkerObserved': requiredLogMarkerObserved,
    'succeeded': succeeded,
  };

  String encode() =>
      '${const JsonEncoder.withIndent('  ').convert(toJson())}\n';
}

LiveAcceptanceEvidence buildLiveAcceptanceEvidence({
  required LiveAcceptanceOptions options,
  required DateTime startedAtUtc,
  required DateTime completedAtUtc,
  required String packagePath,
  required List<String> requiredCases,
  required Set<String> observedCases,
  required List<String> failures,
  required LiveAcceptanceLastRun? lastRun,
  required bool requiredLogMarkerObserved,
}) {
  const acceptanceId = 'dev.topiaforge.sdk-acceptance';
  final acceptancePackage = lastRun?.package(acceptanceId);
  final requiredPackage = options.releaseJourneyEnabled
      ? lastRun?.package(options.requiredLoadedPackageId)
      : null;
  final missing = requiredCases
      .where((caseId) => !observedCases.contains(caseId))
      .toList(growable: false);
  final passed = observedCases.toList()..sort();
  final releaseJourneySucceeded =
      !options.releaseJourneyEnabled ||
      (requiredLogMarkerObserved &&
          requiredPackage?.valid == true &&
          requiredPackage?.status == 'loaded');
  final succeeded =
      missing.isEmpty &&
      failures.isEmpty &&
      acceptancePackage?.valid == true &&
      acceptancePackage?.status == 'loaded' &&
      (lastRun?.rootError.trim().isEmpty ?? false) &&
      releaseJourneySucceeded;
  return LiveAcceptanceEvidence(
    startedAtUtc: startedAtUtc,
    completedAtUtc: completedAtUtc,
    gameDirectory: options.gameDirectory,
    packagePath: packagePath,
    requiredCases: List.unmodifiable(requiredCases),
    passedCases: List.unmodifiable(passed),
    missingCases: List.unmodifiable(missing),
    failures: List.unmodifiable(failures),
    lastRunSessionId: lastRun?.sessionId ?? '',
    acceptancePackageStatus: acceptancePackage?.status ?? 'missing',
    releaseJourneyEnabled: options.releaseJourneyEnabled,
    releaseJourneyCli: options.devCliPath,
    releaseJourneyProject: options.devProjectPath,
    requiredLoadedPackageId: options.requiredLoadedPackageId,
    requiredLoadedPackageStatus: requiredPackage?.status ?? 'not-required',
    requiredLogMarker: options.requiredLogMarker,
    requiredLogMarkerObserved: requiredLogMarkerObserved,
    succeeded: succeeded,
  );
}
