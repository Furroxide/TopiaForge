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
    required this.sourceSha256,
    required this.criticalFiles,
    required this.receiptValid,
  });

  final String id;
  final String status;
  final bool valid;
  final String sourceSha256;
  final List<LiveAcceptanceFileDigest> criticalFiles;
  final bool receiptValid;

  factory LiveAcceptancePackageOutcome.fromJson(Object? value) {
    if (value is! Map) {
      return const LiveAcceptancePackageOutcome(
        id: '',
        status: 'missing',
        valid: false,
        sourceSha256: '',
        criticalFiles: [],
        receiptValid: false,
      );
    }
    final sourceSha256 = value['sourceSha256'];
    final rawCriticalFiles = value['criticalFiles'];
    final criticalFiles = <LiveAcceptanceFileDigest>[];
    final paths = <String>{};
    var receiptValid =
        sourceSha256 is String &&
        _lowerSha256.hasMatch(sourceSha256) &&
        rawCriticalFiles is List &&
        rawCriticalFiles.isNotEmpty &&
        rawCriticalFiles.length <= 8192;
    if (rawCriticalFiles is List && rawCriticalFiles.length <= 8192) {
      String? previousPath;
      for (final rawFile in rawCriticalFiles) {
        final file = LiveAcceptanceFileDigest.tryParse(rawFile);
        if (file == null ||
            !paths.add(file.path) ||
            (previousPath != null && previousPath.compareTo(file.path) >= 0)) {
          receiptValid = false;
          continue;
        }
        previousPath = file.path;
        criticalFiles.add(file);
      }
    }
    return LiveAcceptancePackageOutcome(
      id: value['id'] is String ? value['id'] as String : '',
      status: value['status'] is String ? value['status'] as String : 'missing',
      valid: value['valid'] == true,
      sourceSha256: sourceSha256 is String ? sourceSha256 : '',
      criticalFiles: List.unmodifiable(criticalFiles),
      receiptValid: receiptValid,
    );
  }

  bool matchesReceipt(LiveAcceptancePackageReceipt receipt) {
    if (!receiptValid ||
        sourceSha256 != receipt.sourceSha256 ||
        criticalFiles.length != receipt.criticalFiles.length) {
      return false;
    }
    for (var index = 0; index < criticalFiles.length; index++) {
      if (criticalFiles[index] != receipt.criticalFiles[index]) return false;
    }
    return true;
  }
}

final RegExp _lowerSha256 = RegExp(r'^[0-9a-f]{64}$');
final RegExp _safeReceiptPath = RegExp(r'^[A-Za-z0-9][A-Za-z0-9._/-]{0,511}$');

/// One exact critical-file receipt from an installed package.
final class LiveAcceptanceFileDigest {
  const LiveAcceptanceFileDigest({required this.path, required this.sha256});

  final String path;
  final String sha256;

  static LiveAcceptanceFileDigest? tryParse(Object? value) {
    if (value is! Map ||
        value['path'] is! String ||
        value['sha256'] is! String) {
      return null;
    }
    final path = value['path'] as String;
    final sha256 = value['sha256'] as String;
    if (!_isSafePath(path) || !_lowerSha256.hasMatch(sha256)) return null;
    return LiveAcceptanceFileDigest(path: path, sha256: sha256);
  }

  static bool _isSafePath(String path) =>
      _safeReceiptPath.hasMatch(path) &&
      !path.contains(r'\') &&
      !path.startsWith('/') &&
      !path.endsWith('/') &&
      !path.contains('//') &&
      !path.split('/').any((segment) => segment == '.' || segment == '..');

  Map<String, Object?> toJson() => {'path': path, 'sha256': sha256};

  @override
  bool operator ==(Object other) =>
      other is LiveAcceptanceFileDigest &&
      other.path == path &&
      other.sha256 == sha256;

  @override
  int get hashCode => Object.hash(path, sha256);
}

/// Exact source archive and critical payload bytes expected in `last-run.json`.
final class LiveAcceptancePackageReceipt {
  const LiveAcceptancePackageReceipt({
    required this.sourceSha256,
    required this.criticalFiles,
  });

  final String sourceSha256;
  final List<LiveAcceptanceFileDigest> criticalFiles;

  Map<String, Object?> toJson() => {
    'sourceSha256': sourceSha256,
    'criticalFiles': criticalFiles.map((value) => value.toJson()).toList(),
  };
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
          decoded['schemaVersion'] != 1 ||
          decoded['sessionId'] is! String ||
          (decoded['sessionId'] as String).trim().isEmpty ||
          (decoded['sessionId'] as String).length > 256 ||
          decoded['rootError'] is! String ||
          rawPackages is! List ||
          rawPackages.isEmpty ||
          rawPackages.length > 4096) {
        return null;
      }
      final packages = rawPackages
          .map(LiveAcceptancePackageOutcome.fromJson)
          .toList(growable: false);
      final packageIds = <String>{};
      if (packages.any(
        (package) =>
            package.id.isEmpty ||
            package.id.length > 128 ||
            !packageIds.add(package.id),
      )) {
        return null;
      }
      return LiveAcceptanceLastRun(
        completedAtUtc: parsed.toUtc(),
        sessionId: decoded['sessionId'] as String,
        rootError: decoded['rootError'] as String,
        packages: List.unmodifiable(packages),
      );
    } on Object {
      return null;
    }
  }
}
