import 'dart:convert';

import 'creator_acceptance_models.dart';
import 'live_acceptance_models.dart';

/// Serializable evidence for one interactive Creator workbench acceptance run.
///
/// This is the authoritative private artifact. The public release descriptor
/// binds it by `acceptanceResultSha256`, exactly as the Proton evidence path
/// binds its own acceptance result.
final class CreatorAcceptanceEvidence {
  const CreatorAcceptanceEvidence({
    required this.startedAtUtc,
    required this.completedAtUtc,
    required this.gameDirectory,
    required this.gameBuild,
    required this.acceptanceChallenge,
    required this.lastRunSessionId,
    required this.creatorPackageStatus,
    required this.creatorPackageReceipt,
    required this.requiredCases,
    required this.passedCases,
    required this.missingCases,
    required this.failures,
    required this.lifecycleCycles,
    required this.minimumLifecycleCycles,
    required this.persistence,
    required this.succeeded,
  });

  final DateTime startedAtUtc;
  final DateTime completedAtUtc;
  final String gameDirectory;
  final String gameBuild;
  final String acceptanceChallenge;
  final String lastRunSessionId;
  final String creatorPackageStatus;
  final CreatorPackageReceipt? creatorPackageReceipt;
  final List<String> requiredCases;
  final List<String> passedCases;
  final List<String> missingCases;
  final List<String> failures;
  final int lifecycleCycles;
  final int minimumLifecycleCycles;
  final CreatorPersistenceObservation? persistence;
  final bool succeeded;

  Map<String, Object?> toJson() => {
    'schemaVersion': 1,
    'suite': 'creator-full',
    'startedAtUtc': startedAtUtc.toUtc().toIso8601String(),
    'completedAtUtc': completedAtUtc.toUtc().toIso8601String(),
    'gameDirectory': gameDirectory,
    'gameBuild': gameBuild,
    'acceptanceChallenge': acceptanceChallenge,
    'lastRunSessionId': lastRunSessionId,
    'creatorPackageStatus': creatorPackageStatus,
    'creatorPackageReceipt': creatorPackageReceipt?.toJson(),
    'requiredCases': requiredCases,
    'passedCases': passedCases,
    'missingCases': missingCases,
    'failures': failures,
    'lifecycleCycles': lifecycleCycles,
    'minimumLifecycleCycles': minimumLifecycleCycles,
    'persistence': persistence?.toJson(),
    'saveStateUnchanged': persistence?.saveUnchanged ?? false,
    'checkpointStateUnchanged': persistence?.checkpointUnchanged ?? false,
    'succeeded': succeeded,
  };

  String encode() =>
      '${const JsonEncoder.withIndent('  ').convert(toJson())}\n';
}

/// Applies the Creator pass rule to one observed run.
///
/// A run passes only when every declared case was observed as a challenge-bound
/// native marker, the recorder reported at least the required lifecycle cycles,
/// save and checkpoint bytes are unchanged across End Session, and the manager
/// recorded a healthy session whose loaded CreatorTools package matches the
/// exact receipt built from the installed payload.
CreatorAcceptanceEvidence buildCreatorAcceptanceEvidence({
  required CreatorAcceptanceOptions options,
  required CreatorAcceptanceSpec spec,
  required DateTime startedAtUtc,
  required DateTime completedAtUtc,
  required List<String> requiredCases,
  required Set<String> observedCases,
  required List<String> failures,
  required LiveAcceptanceLastRun? lastRun,
  required String acceptanceChallenge,
  required int lifecycleCycles,
  required CreatorPersistenceObservation? persistence,
  required CreatorPackageReceipt? expectedCreatorReceipt,
}) {
  final creatorPackage = lastRun?.package(CreatorAcceptanceOptions.creatorModId);
  final missing = requiredCases
      .where((caseId) => !observedCases.contains(caseId))
      .toList(growable: false);
  final passed = observedCases.toList()..sort();
  final receiptMatches =
      expectedCreatorReceipt != null &&
      creatorPackage?.matchesReceipt(expectedCreatorReceipt) == true;
  final succeeded =
      missing.isEmpty &&
      failures.isEmpty &&
      lifecycleCycles >= spec.minimumLifecycleCycles &&
      persistence != null &&
      persistence.saveUnchanged &&
      persistence.checkpointUnchanged &&
      persistence.layout.version == CreatorPersistenceLayout.currentVersion &&
      creatorPackage?.valid == true &&
      creatorPackage?.status == 'loaded' &&
      receiptMatches &&
      (lastRun?.rootError.trim().isEmpty ?? false) &&
      (lastRun?.sessionId.trim().isNotEmpty ?? false);
  return CreatorAcceptanceEvidence(
    startedAtUtc: startedAtUtc,
    completedAtUtc: completedAtUtc,
    gameDirectory: options.gameDirectory,
    gameBuild: spec.gameBuild,
    acceptanceChallenge: acceptanceChallenge,
    lastRunSessionId: lastRun?.sessionId ?? '',
    creatorPackageStatus: creatorPackage?.status ?? 'missing',
    creatorPackageReceipt: creatorPackage?.receiptValid == true
        ? CreatorPackageReceipt(
            sourceSha256: creatorPackage!.sourceSha256,
            criticalFiles: creatorPackage.criticalFiles,
          )
        : null,
    requiredCases: List.unmodifiable(requiredCases),
    passedCases: List.unmodifiable(passed),
    missingCases: List.unmodifiable(missing),
    failures: List.unmodifiable(failures),
    lifecycleCycles: lifecycleCycles,
    minimumLifecycleCycles: spec.minimumLifecycleCycles,
    persistence: persistence,
    succeeded: succeeded,
  );
}
