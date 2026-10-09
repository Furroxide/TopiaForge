import 'release_readiness.dart';

/// Private preparation can defer game review until candidate bytes exist.
/// It never grants publication permission.
///
/// The deferral covers P0-GAME-01 only while that gate is blocking. Under the
/// owner's 2026-09-24 disposition it is advisory, so it never blocks and
/// `deferredGateIds` is empty; the code stays so that restoring the gate to
/// blocking restores the deferral without another change here.
final class ReleasePrerequisites {
  ReleasePrerequisites(this.decision);

  final ReleaseReadinessDecision decision;
  bool get isEligible => !decision.gates.any(
    (gate) => gate.id != 'P0-GAME-01' && gate.blocksRelease,
  );
  Map<String, Object?> toPublicSummary() => {
    'schema': 'release-prerequisites-summary-v1',
    'releaseVersion': decision.releaseVersion,
    'targetSha': decision.targetSha,
    'baseReadinessSha256': decision.readinessBlobSha256,
    'status': isEligible ? 'eligible-for-private-build' : 'blocked',
    'gates': decision.gates.map((gate) => gate.toPublicSummary()).toList(),
    'deferredGateIds': [
      for (final gate in decision.gates)
        if (gate.id == 'P0-GAME-01' && gate.blocksRelease) gate.id,
    ],
  };
}
