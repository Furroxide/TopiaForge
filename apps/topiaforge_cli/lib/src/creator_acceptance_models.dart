import 'dart:convert';

import 'live_acceptance_models.dart';

/// Stable Creator-acceptance failure with remediation and a docs anchor.
final class CreatorAcceptanceError implements Exception {
  const CreatorAcceptanceError(this.code, this.cause, this.remediation);

  final String code;
  final String cause;
  final String remediation;

  @override
  String toString() =>
      '$code: $cause Remediation: $remediation '
      'See docs/LiveGameAcceptance.md#${code.toLowerCase()}.';
}

/// Validated inputs for one interactive Creator workbench acceptance run.
final class CreatorAcceptanceOptions {
  const CreatorAcceptanceOptions({
    required this.repositoryRoot,
    required this.gameDirectory,
    required this.outputDirectory,
    required this.timeout,
    this.requiredCases = const [],
    this.requireAll = false,
    this.skipRuntimeInstall = false,
    this.skipLaunch = false,
  });

  /// Canonical id of the mod that hosts the creator workbench. The standalone
  /// global-host package was retired with the Robotopia Creator release; the
  /// workbench now ships inside Sandbox.
  static const String creatorModId =
      'io.github.furroxide.topiaforge.sandbox';

  /// First-party ids are stored under a shortened config stem by
  /// `ManagerPaths.GetConfigPath`; mirror that derivation exactly.
  static const String _firstPartyIdPrefix = 'io.github.furroxide.';

  final String repositoryRoot;
  final String gameDirectory;
  final String outputDirectory;
  final List<String> requiredCases;
  final Duration timeout;
  final bool requireAll;
  final bool skipRuntimeInstall;
  final bool skipLaunch;

  /// Config file stem the manager derives for [creatorModId].
  static String get creatorConfigFileName {
    const id = creatorModId;
    final stem = id.startsWith(_firstPartyIdPrefix)
        ? id.substring(_firstPartyIdPrefix.length)
        : id;
    return '$stem.json';
  }
}

/// Canonical `creatorAcceptance` section of `tests/live-game-acceptance.json`.
final class CreatorAcceptanceSpec {
  const CreatorAcceptanceSpec({
    required this.caseIds,
    required this.gameBuild,
    required this.minimumLifecycleCycles,
  });

  static const int maximumCases = 512;

  final List<String> caseIds;
  final String gameBuild;
  final int minimumLifecycleCycles;

  factory CreatorAcceptanceSpec.fromJson(Object? decoded) {
    if (decoded is! Map<String, Object?> || decoded['schemaVersion'] != 1) {
      throw const CreatorAcceptanceError(
        'TFCREATOR103',
        'Unsupported or malformed acceptance specification.',
        'Update the harness and specification together.',
      );
    }
    final creator = decoded['creatorAcceptance'];
    if (creator is! Map<String, Object?>) {
      throw const CreatorAcceptanceError(
        'TFCREATOR103',
        'The specification has no creatorAcceptance section.',
        'Update the harness and specification together.',
      );
    }
    final gameBuild = creator['gameBuild'];
    final minimumCycles = creator['minimumLifecycleCycles'];
    final cases = creator['cases'];
    if (gameBuild is! String ||
        gameBuild.trim().isEmpty ||
        minimumCycles is! int ||
        minimumCycles < 1 ||
        cases is! List ||
        cases.isEmpty ||
        cases.length > maximumCases) {
      throw const CreatorAcceptanceError(
        'TFCREATOR103',
        'The creatorAcceptance section is invalid.',
        'Update the harness and specification together.',
      );
    }
    final ids = <String>[];
    final unique = <String>{};
    for (final value in cases) {
      final id = value is Map ? value['id'] : null;
      if (id is! String ||
          !RegExp(r'^[a-z0-9][a-z0-9._-]{0,127}$').hasMatch(id) ||
          !unique.add(id)) {
        throw const CreatorAcceptanceError(
          'TFCREATOR103',
          'The creatorAcceptance section contains an invalid case id.',
          'Update the harness and specification together.',
        );
      }
      ids.add(id);
    }
    return CreatorAcceptanceSpec(
      caseIds: List.unmodifiable(ids),
      gameBuild: gameBuild,
      minimumLifecycleCycles: minimumCycles,
    );
  }

  static CreatorAcceptanceSpec decode(String source) {
    try {
      return CreatorAcceptanceSpec.fromJson(jsonDecode(source));
    } on CreatorAcceptanceError {
      rethrow;
    } on Object {
      throw const CreatorAcceptanceError(
        'TFCREATOR103',
        'The acceptance specification is not valid JSON.',
        'Update the harness and specification together.',
      );
    }
  }
}

/// One digest of a persistence root member observed before/after End Session.
final class CreatorStateDigest {
  const CreatorStateDigest({required this.sha256, required this.size});

  final String sha256;
  final int size;

  Map<String, Object?> toJson() => {'sha256': sha256, 'size': size};

  static CreatorStateDigest? tryParse(Object? value) {
    if (value is! Map) return null;
    final sha256 = value['sha256'];
    final size = value['size'];
    if (sha256 is! String ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(sha256) ||
        size is! int ||
        size < 0) {
      return null;
    }
    return CreatorStateDigest(sha256: sha256, size: size);
  }

  @override
  bool operator ==(Object other) =>
      other is CreatorStateDigest &&
      other.sha256 == sha256 &&
      other.size == size;

  @override
  int get hashCode => Object.hash(sha256, size);
}

/// Declared persistence roots and volatile exclusions used for state capture.
///
/// Robotopia build-2309 keeps no save file on disk: its persistent data path
/// holds only telemetry and logs, and PlayerPrefs holds only screen settings.
/// The probe therefore digests whole declared roots minus a declared volatile
/// exclusion set, so a save file introduced by a later game build is captured
/// automatically instead of silently ignored. Both lists are recorded in the
/// evidence so a verifier can reject a probe that narrowed its own scope.
final class CreatorPersistenceLayout {
  const CreatorPersistenceLayout({
    required this.version,
    required this.roots,
    required this.exclusions,
  });

  /// Bumped whenever the declared roots or exclusions change meaning.
  static const int currentVersion = 1;

  final int version;
  final List<String> roots;
  final List<String> exclusions;

  Map<String, Object?> toJson() => {
    'version': version,
    'roots': roots,
    'exclusions': exclusions,
  };

  static CreatorPersistenceLayout? tryParse(Object? value) {
    if (value is! Map) return null;
    final version = value['version'];
    final roots = value['roots'];
    final exclusions = value['exclusions'];
    if (version is! int ||
        version < 1 ||
        roots is! List ||
        roots.isEmpty ||
        roots.length > 64 ||
        exclusions is! List ||
        exclusions.length > 256 ||
        roots.any((root) => root is! String || root.trim().isEmpty) ||
        exclusions.any((value) => value is! String || value.trim().isEmpty)) {
      return null;
    }
    return CreatorPersistenceLayout(
      version: version,
      roots: List.unmodifiable(roots.cast<String>()),
      exclusions: List.unmodifiable(exclusions.cast<String>()),
    );
  }
}

/// Save and checkpoint state observed either side of End Session & Restore.
final class CreatorPersistenceObservation {
  const CreatorPersistenceObservation({
    required this.layout,
    required this.saveBefore,
    required this.saveAfter,
    required this.checkpointBefore,
    required this.checkpointAfter,
  });

  final CreatorPersistenceLayout layout;
  final CreatorStateDigest saveBefore;
  final CreatorStateDigest saveAfter;
  final CreatorStateDigest checkpointBefore;
  final CreatorStateDigest checkpointAfter;

  bool get saveUnchanged => saveBefore == saveAfter;
  bool get checkpointUnchanged => checkpointBefore == checkpointAfter;

  Map<String, Object?> toJson() => {
    'layout': layout.toJson(),
    'save': {
      'before': saveBefore.toJson(),
      'after': saveAfter.toJson(),
      'unchanged': saveUnchanged,
    },
    'checkpoint': {
      'before': checkpointBefore.toJson(),
      'after': checkpointAfter.toJson(),
      'unchanged': checkpointUnchanged,
    },
  };
}

/// One `TF-CREATOR` marker parsed from a structured manager log line.
final class CreatorAcceptanceMarker {
  const CreatorAcceptanceMarker({
    required this.passed,
    required this.challenge,
    required this.caseId,
    required this.detail,
  });

  final bool passed;
  final String challenge;
  final String caseId;
  final String detail;
}

/// Canonical marker prefix emitted by the native creator-workbench recorder.
const String creatorMarkerPrefix = 'TF-CREATOR';

final RegExp _creatorPassPattern = RegExp(
  r'^TF-CREATOR\|PASS\|([0-9a-f]{64})\|'
  r'([a-z0-9][a-z0-9._-]{0,127})\|([^|\r\n]*)$',
);
final RegExp _creatorFailPattern = RegExp(
  r'^TF-CREATOR\|FAIL\|([0-9a-f]{64})\|'
  r'([a-z0-9][a-z0-9._-]{0,127})\|([^|\r\n]+)$',
);
final RegExp _creatorCyclePattern = RegExp(
  r'^TF-CREATOR\|CYCLE\|([0-9a-f]{64})\|([0-9]{1,4})$',
);
final RegExp _creatorStatePattern = RegExp(
  r'^TF-CREATOR\|STATE\|([0-9a-f]{64})\|(before|after)\|'
  r'([0-9a-f]{64})\|([0-9]{1,19})$',
);

/// Parses a PASS/FAIL creator marker, or null when the line is unrelated.
CreatorAcceptanceMarker? tryParseCreatorMarker(String message) {
  final passed = _creatorPassPattern.firstMatch(message);
  if (passed != null) {
    return CreatorAcceptanceMarker(
      passed: true,
      challenge: passed.group(1)!,
      caseId: passed.group(2)!,
      detail: passed.group(3)!,
    );
  }
  final failed = _creatorFailPattern.firstMatch(message);
  if (failed != null) {
    return CreatorAcceptanceMarker(
      passed: false,
      challenge: failed.group(1)!,
      caseId: failed.group(2)!,
      detail: failed.group(3)!,
    );
  }
  return null;
}

/// Parses a completed-lifecycle-cycle marker, or null when unrelated.
({String challenge, int cycle})? tryParseCreatorCycle(String message) {
  final match = _creatorCyclePattern.firstMatch(message);
  if (match == null) return null;
  final cycle = int.tryParse(match.group(2)!);
  if (cycle == null || cycle < 0) return null;
  return (challenge: match.group(1)!, cycle: cycle);
}

/// Parses an in-game checkpoint-state digest marker, or null when unrelated.
///
/// Robotopia keeps checkpoint progress in memory (`CheckpointManager`), so the
/// only truthful observer of checkpoint bytes is the native recorder. It emits
/// one digest before the first Open and one after the final End Session.
({String challenge, bool isBefore, CreatorStateDigest digest})?
tryParseCreatorState(String message) {
  final match = _creatorStatePattern.firstMatch(message);
  if (match == null) return null;
  final size = int.tryParse(match.group(4)!);
  if (size == null || size < 0) return null;
  return (
    challenge: match.group(1)!,
    isBefore: match.group(2)! == 'before',
    digest: CreatorStateDigest(sha256: match.group(3)!, size: size),
  );
}

/// Re-exported so the Creator runner shares the live-acceptance receipt shape.
typedef CreatorPackageReceipt = LiveAcceptancePackageReceipt;

/// Re-exported so the Creator runner shares the live-acceptance digest shape.
typedef CreatorFileDigest = LiveAcceptanceFileDigest;
