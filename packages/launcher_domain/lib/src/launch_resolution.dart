import 'models.dart';
import 'versioning.dart';

part 'launch_resolution/launch_resolver.dart';
part 'launch_resolution/world_and_transition.dart';

/// Why a launch cannot proceed. A closed set, shared with the C# manager.
///
/// Closed on purpose. An open-ended string reason is one a caller cannot switch
/// on, cannot translate, and cannot present as anything better than the sentence
/// a developer happened to write. Every one of these is something a player or an
/// author can act on.
///
/// The order is the sort order, and it is part of the contract: the resolver
/// reports every applicable reason rather than the first, so without a total
/// order the same profile yields the same reasons in a different sequence and
/// every shared fixture is flaky.
enum LaunchBlockCode {
  /// No launch target with that id exists in the effective profile.
  ///
  /// Also where a selection saved before a target was renamed lands.
  targetNotDeclared,

  /// The declaring package is installed but not enabled in this profile.
  targetPackageDisabled,

  /// A pinned version sits outside a range on the reference path.
  targetPackageVersionUnsatisfied,

  /// The target names a gamemode no package in the profile declares.
  gamemodeNotDeclared,

  /// The gamemode's package is installed but not enabled.
  gamemodePackageDisabled,

  /// A cross-package gamemode reference no required dependency owns.
  gamemodeRefNotADependency,

  /// Declared and loaded, but the implementation did not bind.
  ///
  /// Runtime observation only. Static resolution cannot see binding and does not
  /// guess at it.
  gamemodeUnbound,

  /// The resolved world id names no declaration.
  worldNotDeclared,

  /// The world's package is installed but not enabled.
  worldPackageDisabled,

  /// A cross-package world reference no required dependency owns.
  worldRefNotADependency,

  /// A fixed or list policy, and the requested world is outside the declared set.
  worldNotAdmittedByPolicy,

  /// An open policy, and the world consents to neither this gamemode nor any.
  worldConsentMissing,

  /// A policy names a discovered family, or an instance under one.
  ///
  /// Members of a discovered family exist only once the game has run and
  /// reported them, so a policy naming one is naming content that may never
  /// appear on this installation.
  worldNotStaticallyDeclared,

  /// The world and the gamemode share no transition.
  transitionUnsatisfiable,

  /// An explicit transition outside the intersection, or a player override the
  /// target does not offer.
  transitionNotOffered,

  /// The gamemode requires an authored spawn marker; the world provides a default.
  spawnRequirementUnsatisfied,

  /// The world's package does not support this platform, architecture, content
  /// target, or game version.
  worldPlatformUnsupported,

  /// Two resolved packages both claim to own an id, and the longer-prefix owner
  /// does not declare it.
  declarationIdAmbiguous,

  /// A target is otherwise valid but every world it admits is unavailable.
  ///
  /// Surfaced with a reason rather than omitted: a bound gamemode that silently
  /// vanishes from a menu is indistinguishable from one never installed.
  noAvailableTarget,

  /// Revalidation before preparing found a loaded package set the plan was not
  /// built from.
  planPackageSetMismatch,
}

/// One reason a launch is blocked, and what it is about.
class LaunchBlock implements Comparable<LaunchBlock> {
  const LaunchBlock(this.code, this.subject, [this.subjectVersion = '']);

  /// Why the launch is blocked.
  final LaunchBlockCode code;

  /// The id the reason is about.
  final String subject;

  /// The pinned version, where the reason is about one.
  final String subjectVersion;

  @override
  int compareTo(LaunchBlock other) {
    final byCode = code.index.compareTo(other.code.index);
    if (byCode != 0) {
      return byCode;
    }
    final bySubject = subject.compareTo(other.subject);
    return bySubject != 0
        ? bySubject
        : subjectVersion.compareTo(other.subjectVersion);
  }

  @override
  bool operator ==(Object other) =>
      other is LaunchBlock &&
      other.code == code &&
      other.subject == subject &&
      other.subjectVersion == subjectVersion;

  @override
  int get hashCode => Object.hash(code, subject, subjectVersion);

  @override
  String toString() => subjectVersion.isEmpty
      ? '${code.name}($subject)'
      : '${code.name}($subject@$subjectVersion)';
}

/// One enabled package at its pinned version, with the manifest read from it.
class ResolvedPackage {
  const ResolvedPackage({
    required this.id,
    required this.version,
    required this.manifest,
  });

  final String id;
  final String version;
  final ModManifest manifest;
}

/// What this installation is, for the package-level compatibility rule.
///
/// Passed in rather than read. Reading the install from disk here is what would
/// make the resolution untestable again: the point is that a resolution can be
/// reproduced from its inputs alone. Any empty value skips that dimension.
class InstallFacts {
  const InstallFacts({
    this.platform = '',
    this.architecture = '',
    this.contentTarget = '',
    this.gameVersion = '',
  });

  final String platform;
  final String architecture;
  final String contentTarget;
  final String gameVersion;
}

/// The enabled package set at pinned versions, and nothing else.
class EffectiveProfile {
  const EffectiveProfile({
    required this.packages,
    this.profileId = '',
    this.revision = 0,
    this.install = const InstallFacts(),
    this.disabledPackages = const [],
  });

  final String profileId;
  final int revision;
  final List<ResolvedPackage> packages;
  final InstallFacts install;

  /// Packages installed but not enabled in this profile.
  ///
  /// Carried so the resolver can tell "there is no such thing" from "it is right
  /// there, switched off". Those are the same dead end to a player who is only
  /// told the target does not exist, and the second is a click from working.
  final List<ResolvedPackage> disabledPackages;
}

/// What the game reported after running. It can only ever narrow a plan.
///
/// An observation may mark a declared target unavailable and may name the
/// members of a discovered family. It may never add a launch target, and never
/// re-enable a package the profile disabled: letting a previous run's report
/// resurrect content is how a launcher ends up offering something the current
/// profile does not contain.
class RuntimeObservation {
  const RuntimeObservation({
    this.unavailableWorldIds = const [],
    this.discoveredWorldIds = const [],
    this.unboundGamemodeIds = const [],
  });

  /// An observation that reports nothing.
  static const none = RuntimeObservation();

  final List<String> unavailableWorldIds;
  final List<String> discoveredWorldIds;
  final List<String> unboundGamemodeIds;
}

/// A request to launch one target.
class LaunchRequest {
  const LaunchRequest({
    required this.targetId,
    this.worldOverride = '',
    this.transitionOverride = '',
  });

  final String targetId;

  /// A world the player chose, or empty for the target's default.
  final String worldOverride;

  /// A transition the player chose, or empty for the target's own choice.
  final String transitionOverride;
}

/// One launch, fully decided.
class LaunchPlan {
  LaunchPlan({
    required this.launchTargetId,
    required this.gamemodeId,
    required this.worldId,
    required this.transition,
    required this.resolvedPackages,
    this.worldInstanceId = '',
  }) : digest = packageSetDigest(resolvedPackages);

  final String launchTargetId;
  final String gamemodeId;
  final String worldId;

  /// The observed instance under a discovered family, or empty.
  final String worldInstanceId;
  final String transition;

  /// The packages this plan was resolved against, ordered by id.
  final List<ResolvedPackage> resolvedPackages;

  /// The digest of [resolvedPackages].
  ///
  /// Revalidated against the actually-loaded set before any scene work begins.
  /// That check is the whole reason the plan carries its package set: a
  /// preflight that agreed with a set nobody compared afterwards is a promise,
  /// not an invariant.
  final String digest;
}

/// A plan, or the reasons there is none.
class LaunchResolution {
  const LaunchResolution._(this.plan, this.blocks);

  factory LaunchResolution.success(LaunchPlan plan) =>
      LaunchResolution._(plan, const []);

  factory LaunchResolution.blocked(List<LaunchBlock> blocks) {
    final ordered = [...blocks]..sort();
    return LaunchResolution._(null, List.unmodifiable(ordered));
  }

  /// The plan, or null when the launch is blocked.
  final LaunchPlan? plan;

  /// Every applicable reason, ordered. Empty when a plan was produced.
  final List<LaunchBlock> blocks;

  bool get resolved => plan != null;
}

/// Reduces a package set to one comparable value.
///
/// FNV-1a over the canonical `id@version` form, written out here and mirrored
/// exactly in `PackageSetDigest` so both sides agree without either taking a
/// dependency for it.
///
/// Not a security boundary and not an integrity check. It answers one question
/// -- is the loaded set the set this plan was built from -- about two views of
/// the same local state. Today nothing asks that question at all, so a cheap
/// exact-equality signal is the whole improvement.
String packageSetDigest(List<ResolvedPackage> packages) {
  final canonical =
      packages.map((item) => '${item.id}@${item.version}').toList()..sort();
  return packageSetDigestOfCanonical(canonical);
}

/// Computes the digest of already-canonical `id@version` entries.
String packageSetDigestOfCanonical(List<String> canonicalEntries) {
  // 64-bit FNV-1a. Dart ints are 64-bit two's complement on native and wrap on
  // overflow exactly as the C# ulong does, so the two produce the same bits;
  // the value is rendered unsigned at the end so they also produce the same
  // text.
  var hash = BigInt.parse('14695981039346656037');
  final prime = BigInt.parse('1099511628211');
  final mask = (BigInt.one << 64) - BigInt.one;

  BigInt mix(BigInt current, int byte) =>
      ((current ^ BigInt.from(byte)) * prime) & mask;

  for (var index = 0; index < canonicalEntries.length; index++) {
    if (index > 0) {
      hash = mix(hash, 0x0a);
    }
    for (final unit in canonicalEntries[index].codeUnits) {
      // UTF-16 code units, low byte first, so both languages hash the same
      // bytes for any id -- including one outside the Basic Latin range.
      hash = mix(hash, unit & 0xFF);
      hash = mix(hash, (unit >> 8) & 0xFF);
    }
  }

  return hash.toRadixString(16).padLeft(16, '0');
}
