part of 'release_readiness.dart';

final class _GateContract {
  const _GateContract({
    required this.id,
    required this.priority,
    required this.enforcement,
    required this.blockedReasonCode,
    required this.reviewerRoles,
    this.acceptedRiskScope,
  });

  final String id;
  final String priority;
  final String enforcement;
  final String blockedReasonCode;
  final List<String> reviewerRoles;
  final String? acceptedRiskScope;
}

// The `0.x` release register keeps every gate visible but only lets a
// `blocking` gate hold a candidate. TopiaForge has never shipped, and eight of
// the original twelve gates wait on organizational evidence — counsel sign-off,
// a paid code-signing certificate, GitHub org administration, external QA
// participants — that an alpha line cannot obtain. Blocking is therefore
// reserved for the four gates that are release-fatal at any version plus the
// one that proves the product runs at all:
//
//   P0-IP-01     rights to integrate with Robotopia
//   P0-OSS-01    third-party redistribution
//   P0-PRIV-01   the unapproved RoboAPI backend dependency
//   P0-CRED-01   rotation of exposed credentials
//   P0-GAME-01   the product loads and runs on the pinned game build
//
// The remaining seven are advisory: recorded, reported, and approvable, but not
// by themselves a stop. Restoring the `1.0` posture is a matter of moving each
// `enforcement` back to `blocking` here and in `release/release-readiness.json`;
// the enforcement value is pinned per gate so the decision file cannot relax
// itself. See docs/LaunchBlockers.md.
const _gateContracts = [
  _GateContract(
    id: 'P0-IP-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: ['ip-trademark-counsel', 'project-owner', 'robotopia-owner'],
  ),
  // Re-opened 2026-08-06. The legal inventory is a fixed allowlist that proves
  // the licence texts it names exist, not that every redistributed asset has a
  // licence, so the readiness decision must carry the gate rather than infer it
  // from the inventory passing. See P0-OSS-01 in docs/LaunchBlockers.md.
  _GateContract(
    id: 'P0-OSS-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: ['ip-trademark-counsel', 'release-owner'],
  ),
  _GateContract(
    id: 'P0-PRIV-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: [
      'backend-owner',
      'privacy-legal',
      'product-owner',
      'robotopia-owner',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-TRUST-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: [
      'product-owner',
      'registry-owner',
      'release-owner',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-CRED-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'rotation-evidence-missing',
    reviewerRoles: ['credential-owner', 'security-owner'],
  ),
  _GateContract(
    id: 'P0-WIN-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'platform-evidence-missing',
    reviewerRoles: ['release-owner', 'windows-release-qa'],
  ),
  // P0-LINUX-01 is intentionally absent: Linux is descoped from 0.1.0-rc.1 and
  // returns in rc.2. Restore this entry, the schema's gate count and id enum,
  // and the policy platform archives together when it does.
  _GateContract(
    id: 'P0-GAME-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'acceptance-evidence-missing',
    reviewerRoles: ['robotopia-owner', 'runtime-mod-qa'],
  ),
  _GateContract(
    id: 'P0-HOST-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'host-evidence-missing',
    reviewerRoles: [
      'credential-owner',
      'github-administrator',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-CAND-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'candidate-evidence-missing',
    reviewerRoles: ['project-owner', 'release-manager'],
  ),
  _GateContract(
    id: 'P1-UX-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'acceptance-evidence-missing',
    reviewerRoles: ['accessibility-reviewer', 'native-qa', 'product-owner'],
    acceptedRiskScope: 'rc1-native-ux-accessibility',
  ),
  _GateContract(
    id: 'P1-E2E-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'independent-evidence-missing',
    reviewerRoles: [
      'external-author-reviewer',
      'external-player-reviewer',
      'release-owner',
    ],
    acceptedRiskScope: 'rc1-independent-player-author-e2e',
  ),
  _GateContract(
    id: 'P1-SUPPORT-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'ownership-evidence-missing',
    reviewerRoles: [
      'incident-owner',
      'release-owner',
      'security-intake-owner',
      'support-owner',
    ],
    acceptedRiskScope: 'rc1-support-incident-ownership',
  ),
];

bool _sameList(List<String> left, List<String> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}

bool _isStrictlySorted(List<String> values) {
  for (var index = 1; index < values.length; index++) {
    if (values[index - 1].compareTo(values[index]) >= 0) return false;
  }
  return true;
}
