import 'dart:convert';

import 'models.dart';
import 'versioning.dart';

part 'launch_resolution/launch_resolver.dart';
part 'launch_resolution/world_and_transition.dart';
part 'launch_resolution/launch_profile.dart';
part 'launch_resolution/launch_plan.dart';
part 'launch_resolution/transport_checks.dart';
part 'launch_resolution/transport_codec.dart';
part 'launch_resolution/observation_models.dart';
part 'launch_resolution/runtime_observation.dart';
part 'launch_resolution/profile_transport.dart';
part 'launch_resolution/outcome_transport.dart';
part 'launch_resolution/owner_index.dart';

/// Shared, closed resolution reasons. Ordering uses their ordinal wire names.
enum LaunchBlockCode {
  targetNotDeclared,
  targetPlatformUnsupported,
  gamemodePlatformUnsupported,
  targetPackageDisabled,
  targetPackageVersionUnsatisfied,
  gamemodeNotDeclared,
  gamemodePackageDisabled,
  gamemodeRefNotADependency,
  gamemodeUnbound,
  worldNotDeclared,
  worldPackageDisabled,
  worldRefNotADependency,
  worldNotAdmittedByPolicy,
  worldConsentMissing,
  worldConsentRefNotADependency,
  worldNotStaticallyDeclared,
  transitionUnsatisfiable,
  transitionNotOffered,
  spawnRequirementUnsatisfied,
  worldPlatformUnsupported,
  declarationIdAmbiguous,
  noAvailableTarget,
  planPackageSetMismatch,
  planResolutionMismatch,
  worldUnbound,
  worldUnavailable,
}

final class LaunchBlock implements Comparable<LaunchBlock> {
  const LaunchBlock(this.code, this.subject, [this.subjectVersion = '']);
  factory LaunchBlock.fromJson(Object? value) {
    final json = _object(
      value,
      {'code', 'subject', 'subjectVersion'},
      {'code', 'subject', 'subjectVersion'},
    );
    final code = _choice(
      json['code'],
      LaunchBlockCode.values.map((item) => item.name),
    );
    return LaunchBlock(
      LaunchBlockCode.values.byName(code),
      _rawString(json['subject']),
      _rawString(json['subjectVersion']),
    );
  }
  final LaunchBlockCode code;
  final String subject;
  final String subjectVersion;
  @override
  int compareTo(LaunchBlock other) {
    final byCode = code.name.compareTo(other.code.name);
    if (byCode != 0) return byCode;
    final bySubject = subject.compareTo(other.subject);
    return bySubject != 0
        ? bySubject
        : subjectVersion.compareTo(other.subjectVersion);
  }

  @override
  bool operator ==(Object other) =>
      other is LaunchBlock &&
      code == other.code &&
      subject == other.subject &&
      subjectVersion == other.subjectVersion;
  @override
  int get hashCode => Object.hash(code, subject, subjectVersion);
  Map<String, Object?> toJson() => {
    'code': code.name,
    'subject': subject,
    'subjectVersion': subjectVersion,
  };
  @override
  String toString() =>
      '${code.name}($subject${subjectVersion.isEmpty ? '' : '@$subjectVersion'})';
}

List<LaunchBlock> _orderedBlocks(Iterable<LaunchBlock> blocks) {
  final result = blocks.toSet().toList()..sort();
  return List.unmodifiable(result);
}

final class LaunchResolution {
  const LaunchResolution._(this.plan, this.blocks);
  factory LaunchResolution.success(LaunchPlan plan) =>
      LaunchResolution._(plan, const []);
  factory LaunchResolution.blocked(Iterable<LaunchBlock> blocks) =>
      LaunchResolution._(null, _orderedBlocks(blocks));
  final LaunchPlan? plan;
  final List<LaunchBlock> blocks;
  bool get resolved => plan != null;
}
