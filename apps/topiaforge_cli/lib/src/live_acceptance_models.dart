import 'dart:convert';

/// Stable live-acceptance failure with remediation and a documentation anchor.
final class LiveAcceptanceError implements Exception {
  const LiveAcceptanceError(this.code, this.cause, this.remediation);

  final String code;
  final String cause;
  final String remediation;

  @override
  String toString() =>
      '$code: $cause Remediation: $remediation '
      'See docs/LiveGameAcceptance.md#${code.toLowerCase()}.';
}

/// Validated inputs for one live Robotopia acceptance run.
final class LiveAcceptanceOptions {
  const LiveAcceptanceOptions({
    required this.repositoryRoot,
    required this.gameDirectory,
    required this.outputDirectory,
    required this.timeout,
    this.packagePath = '',
    this.requiredCases = const [],
    this.requireAll = false,
    this.skipRuntimeInstall = false,
    this.skipLaunch = false,
    this.devCliPath = '',
    this.devProjectPath = '',
    this.requiredLoadedPackageId = '',
    this.requiredLogMarker = '',
  });

  final String repositoryRoot;
  final String gameDirectory;
  final String packagePath;
  final String outputDirectory;
  final List<String> requiredCases;
  final Duration timeout;
  final String devCliPath;
  final String devProjectPath;
  final String requiredLoadedPackageId;
  final String requiredLogMarker;
  final bool requireAll;
  final bool skipRuntimeInstall;
  final bool skipLaunch;

  bool get releaseJourneyEnabled => [
    devCliPath,
    devProjectPath,
    requiredLoadedPackageId,
    requiredLogMarker,
  ].every((value) => value.trim().isNotEmpty);
}

/// Canonical case ids loaded from `tests/live-game-acceptance.json`.
final class LiveAcceptanceSpec {
  const LiveAcceptanceSpec(this.caseIds);

  static const int maximumCases = 512;

  final List<String> caseIds;

  factory LiveAcceptanceSpec.fromJson(Object? decoded) {
    if (decoded is! Map<String, Object?>) {
      throw const LiveAcceptanceError(
        'TFACCEPT103',
        'The acceptance specification is not a JSON object.',
        'Update the harness and specification together.',
      );
    }
    if (decoded['schemaVersion'] != 1) {
      throw LiveAcceptanceError(
        'TFACCEPT103',
        'Unsupported acceptance specification schema '
            '${decoded['schemaVersion']}.',
        'Update the harness and specification together.',
      );
    }
    final cases = decoded['cases'];
    if (cases is! List || cases.isEmpty || cases.length > maximumCases) {
      throw const LiveAcceptanceError(
        'TFACCEPT103',
        'The acceptance specification has an invalid case collection.',
        'Update the harness and specification together.',
      );
    }
    final ids = <String>[];
    final unique = <String>{};
    for (final value in cases) {
      final id = value is Map ? value['id'] : null;
      if (id is! String ||
          id.trim().isEmpty ||
          id.length > 128 ||
          !unique.add(id)) {
        throw const LiveAcceptanceError(
          'TFACCEPT103',
          'The acceptance specification contains an invalid case id.',
          'Update the harness and specification together.',
        );
      }
      ids.add(id);
    }
    return LiveAcceptanceSpec(List.unmodifiable(ids));
  }

  static LiveAcceptanceSpec decode(String source) {
    try {
      return LiveAcceptanceSpec.fromJson(jsonDecode(source));
    } on LiveAcceptanceError {
      rethrow;
    } on Object {
      throw const LiveAcceptanceError(
        'TFACCEPT103',
        'The acceptance specification is not valid JSON.',
        'Update the harness and specification together.',
      );
    }
  }
}

/// One package outcome from the manager's `last-run.json` evidence.
final class LiveAcceptancePackageOutcome {
  const LiveAcceptancePackageOutcome({
    required this.id,
    required this.status,
    required this.valid,
  });

  final String id;
  final String status;
  final bool valid;

  factory LiveAcceptancePackageOutcome.fromJson(Object? value) {
    if (value is! Map) {
      return const LiveAcceptancePackageOutcome(
        id: '',
        status: 'missing',
        valid: false,
      );
    }
    return LiveAcceptancePackageOutcome(
      id: value['id'] is String ? value['id'] as String : '',
      status: value['status'] is String ? value['status'] as String : 'missing',
      valid: value['valid'] == true,
    );
  }
}

/// Fresh manager evidence relevant to the acceptance decision.
final class LiveAcceptanceLastRun {
  const LiveAcceptanceLastRun({
    required this.completedAtUtc,
    required this.sessionId,
    required this.rootError,
    required this.packages,
  });

  final DateTime completedAtUtc;
  final String sessionId;
  final String rootError;
  final List<LiveAcceptancePackageOutcome> packages;

  LiveAcceptancePackageOutcome? package(String id) {
    for (final package in packages) {
      if (package.id == id) return package;
    }
    return null;
  }

  static LiveAcceptanceLastRun? tryParse(String source) {
    try {
      final decoded = jsonDecode(source);
      if (decoded is! Map) return null;
      final completed = decoded['completedAtUtc'];
      final parsed = completed is String ? DateTime.tryParse(completed) : null;
      final rawPackages = decoded['packages'];
      if (parsed == null ||
          decoded['sessionId'] is! String ||
          decoded['rootError'] is! String ||
          rawPackages is! List ||
          rawPackages.length > 4096) {
        return null;
      }
      return LiveAcceptanceLastRun(
        completedAtUtc: parsed.toUtc(),
        sessionId: decoded['sessionId'] as String,
        rootError: decoded['rootError'] as String,
        packages: List.unmodifiable(
          rawPackages.map(LiveAcceptancePackageOutcome.fromJson),
        ),
      );
    } on Object {
      return null;
    }
  }
}
