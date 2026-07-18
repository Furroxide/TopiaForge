part of '../local_launcher_repository.dart';

const _maxPackageInboxEntries = 1024;
const _maxPackageInboxCandidates = 256;

extension _PackageInbox on LocalLauncherRepository {
  Future<PackageInboxInstallOutcome> _installInboxPackages(
    GameInstall install,
  ) async {
    final issues = <LauncherIssue>[];
    final inbox = _packageInbox(install);
    final enumeration = await _enumerateInbox(inbox);
    issues.addAll(enumeration.issues);
    if (enumeration.blocked) {
      await _logInboxOutcome(issues, candidateCount: enumeration.files.length);
      return _inboxOutcome(
        candidateCount: enumeration.files.length,
        issues: issues,
      );
    }
    if (enumeration.files.isEmpty) {
      await _appendLauncherLogBestEffort('Package inbox is empty.');
      return _inboxOutcome(candidateCount: 0);
    }

    final GameInstall currentInstall;
    try {
      currentInstall = await _validateGameDirectory(install.path);
      final blockers = currentInstall.issues.where((issue) => issue.isBlocking);
      if (blockers.isNotEmpty) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: 'package-inbox',
            message:
                'TFINBOX101: Package inbox processing requires a valid game '
                'install. ${blockers.map((issue) => issue.message).join(' ')}',
          ),
        );
        await _logInboxOutcome(
          issues,
          candidateCount: enumeration.files.length,
        );
        return _inboxOutcome(
          candidateCount: enumeration.files.length,
          issues: issues,
        );
      }
    } on Object catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: 'package-inbox',
          message:
              'TFINBOX101: Could not validate the game install before '
              'processing the package inbox: $error',
        ),
      );
      await _logInboxOutcome(issues, candidateCount: enumeration.files.length);
      return _inboxOutcome(
        candidateCount: enumeration.files.length,
        issues: issues,
      );
    }

    final candidates = <_InboxCandidate>[];
    for (final file in enumeration.files) {
      final candidate = await _preflightInboxCandidate(file, currentInstall);
      candidates.add(candidate);
      if (!candidate.isValid) {
        issues.add(candidate.issue);
      }
    }
    final selection = await _assessInboxSelection(candidates, currentInstall);
    issues.addAll(selection.issues);

    final groups = <String, List<_InboxCandidate>>{};
    for (final candidate in candidates) {
      groups.putIfAbsent(candidate.groupKey, () => []).add(candidate);
    }
    final groupKeys = groups.keys.toList()..sort();
    var installedCount = 0;
    var supersededCount = 0;
    var consumedCount = 0;
    final invalidCount =
        candidates.where((candidate) => !candidate.isValid).length +
        selection.rejected.length;
    var installFailureCount = 0;
    var consumptionFailureCount = 0;
    final pending = <_PendingInboxGroup>[];
    final completed = <_PendingInboxGroup>[];

    for (final groupKey in groupKeys) {
      final ordered = groups[groupKey]!..sort(_compareInboxPaths);
      final selected = selection.selected[groupKey];
      final alternatives =
          ordered
              .where(
                (candidate) =>
                    candidate.isValid &&
                    !selection.rejected.contains(candidate),
              )
              .toList()
            ..sort(_compareInboxSelection);
      if (selected == null || !alternatives.remove(selected)) continue;
      final selectable = <_InboxCandidate>[selected, ...alternatives];

      supersededCount += selectable.length - 1;
      pending.add(_PendingInboxGroup(selectable));
    }

    // A consumer can sort before a provider by id/path. Retry failed groups
    // after every successful install so local inbox dependencies can unblock
    // one another without making filesystem enumeration order observable.
    final maxPasses = pending.length;
    for (var pass = 0; pass < maxPasses && pending.isNotEmpty; pass++) {
      var madeProgress = false;
      for (var index = 0; index < pending.length;) {
        final group = pending[index];
        final winner = group.winner;
        try {
          await _installPackage(
            winner.file.path,
            currentInstall,
            expectedSha256: winner.sha256,
            rootSourceKind: 'inbox',
          );
          installedCount += 1;
          madeProgress = true;
          pending.removeAt(index);
          completed.add(group);
        } on Object catch (error) {
          group.lastError = error;
          index += 1;
          continue;
        }
      }
      if (!madeProgress) break;
    }
    for (final group in pending) {
      installFailureCount += 1;
      final winner = group.winner;
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: winner.fileName,
          message:
              'TFINBOX120: ${winner.fileName} passed preflight but could '
              'not be installed atomically. It and its valid alternatives '
              'were retained for retry. ${group.lastError}',
        ),
      );
    }

    // Successful winners are safe to consume immediately. Superseded
    // alternatives are consumed only after every selected root succeeds, so a
    // partial batch keeps the versions needed for a deterministic retry.
    final batchComplete = pending.isEmpty;
    for (final group in completed) {
      final consumable = batchComplete
          ? group.selectable
          : <_InboxCandidate>[group.winner];
      for (final candidate in consumable) {
        final consumeError = await _consumeInboxCandidate(candidate);
        if (consumeError == null) {
          consumedCount += 1;
        } else {
          consumptionFailureCount += 1;
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.warning,
              subjectId: candidate.fileName,
              message:
                  'TFINBOX130: ${candidate.fileName} installed or was '
                  'superseded, but could not be moved out of the inbox '
                  'pattern. Close programs using the file and try again. '
                  '$consumeError',
            ),
          );
        }
      }
    }

    await _logInboxOutcome(
      issues,
      candidateCount: candidates.length,
      installedCount: installedCount,
      consumedCount: consumedCount,
    );
    return _inboxOutcome(
      candidateCount: candidates.length,
      installedCount: installedCount,
      supersededCount: supersededCount,
      consumedCount: consumedCount,
      invalidCount: invalidCount,
      installFailureCount: installFailureCount,
      consumptionFailureCount: consumptionFailureCount,
      issues: issues,
    );
  }

  Future<_InboxEnumeration> _enumerateInbox(Directory inbox) async {
    final type = FileSystemEntity.typeSync(inbox.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return const _InboxEnumeration(files: []);
    }
    if (type != FileSystemEntityType.directory) {
      return _InboxEnumeration(
        files: const [],
        blocked: true,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: 'package-inbox',
            message:
                'TFINBOX100: The package inbox is not a regular directory.',
          ),
        ],
      );
    }

    final files = <File>[];
    var entryCount = 0;
    try {
      await for (final entity in inbox.list(followLinks: false)) {
        entryCount += 1;
        if (entryCount > _maxPackageInboxEntries) {
          return _InboxEnumeration(
            files: files,
            blocked: true,
            issues: const [
              LauncherIssue(
                severity: IssueSeverity.error,
                subjectId: 'package-inbox',
                message:
                    'TFINBOX102: The package inbox exceeds its 1024-entry '
                    'safety limit. Remove unrelated files and try again.',
              ),
            ],
          );
        }
        if (!entity.path.toLowerCase().endsWith('.topiaforgemod') ||
            FileSystemEntity.typeSync(entity.path, followLinks: false) !=
                FileSystemEntityType.file) {
          continue;
        }
        files.add(File(entity.path));
        if (files.length > _maxPackageInboxCandidates) {
          return _InboxEnumeration(
            files: files,
            blocked: true,
            issues: const [
              LauncherIssue(
                severity: IssueSeverity.error,
                subjectId: 'package-inbox',
                message:
                    'TFINBOX103: The package inbox exceeds its 256-package '
                    'safety limit. Process packages in smaller batches.',
              ),
            ],
          );
        }
      }
    } on Object catch (error) {
      return _InboxEnumeration(
        files: files,
        blocked: true,
        issues: [
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: 'package-inbox',
            message:
                'TFINBOX104: Could not enumerate the package inbox: $error',
          ),
        ],
      );
    }
    files.sort((left, right) {
      final pathOrder = _normalizedInboxPath(
        left.path,
      ).compareTo(_normalizedInboxPath(right.path));
      return pathOrder != 0 ? pathOrder : left.path.compareTo(right.path);
    });
    return _InboxEnumeration(files: files);
  }

  Future<_InboxCandidate> _preflightInboxCandidate(
    File file,
    GameInstall install,
  ) async {
    ModManifest? manifest;
    var packageSha256 = '';
    try {
      final package = await _readPackage(file.path);
      manifest = package.manifest;
      packageSha256 = package.sha256Hex;
      final errors = <String>[
        ...manifest
            .validate()
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
        ..._dependencyPlanner
            .runtimeCompatibilityIssues(
              manifest,
              gameVersion: install.gameVersion,
              requireKnownGameVersion: true,
              loaderVersion: _loaderVersion,
              sdkVersion: _sdkVersion,
              platform: _gamePlatform(install),
              architecture: _gameArchitecture(install),
              contentTargets: _gameContentTargets(install),
            )
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
      ];
      if (errors.isNotEmpty) {
        return _InboxCandidate.rejected(
          file,
          manifest,
          errors,
          sha256: packageSha256,
        );
      }

      final stagingRoot = _managerStaging(install)..createSync(recursive: true);
      final extracted = stagingRoot.createTempSync('launcher-inbox-preflight-');
      try {
        package.archive.extractTo(extracted);
        await _validatePackageMetadataBeforeCommit(extracted);
      } finally {
        if (extracted.existsSync()) extracted.deleteSync(recursive: true);
      }
      return _InboxCandidate.valid(file, manifest, packageSha256);
    } on Object catch (error) {
      return _InboxCandidate.rejected(file, manifest, [
        error.toString(),
      ], sha256: packageSha256);
    }
  }

  Future<void> _logInboxOutcome(
    List<LauncherIssue> issues, {
    required int candidateCount,
    int installedCount = 0,
    int consumedCount = 0,
  }) async {
    await _appendLauncherLogBestEffort(
      'Processed package inbox: $candidateCount candidate(s), '
      '$installedCount installed, $consumedCount consumed, '
      '${issues.length} issue(s).',
    );
    for (final issue in issues) {
      await _appendLauncherLogBestEffort(
        'Package inbox ${issue.severity.name}: ${issue.message}',
      );
    }
  }
}

PackageInboxInstallOutcome _inboxOutcome({
  required int candidateCount,
  int installedCount = 0,
  int supersededCount = 0,
  int consumedCount = 0,
  int invalidCount = 0,
  int installFailureCount = 0,
  int consumptionFailureCount = 0,
  List<LauncherIssue> issues = const [],
}) => PackageInboxInstallOutcome(
  candidateCount: candidateCount,
  installedCount: installedCount,
  supersededCount: supersededCount,
  consumedCount: consumedCount,
  invalidCount: invalidCount,
  installFailureCount: installFailureCount,
  consumptionFailureCount: consumptionFailureCount,
  issues: issues,
);

class _InboxEnumeration {
  const _InboxEnumeration({
    required this.files,
    this.blocked = false,
    this.issues = const [],
  });

  final List<File> files;
  final bool blocked;
  final List<LauncherIssue> issues;
}

class _InboxCandidate {
  _InboxCandidate._({
    required this.file,
    required this.manifest,
    required this.errors,
    required this.sha256,
  }) : normalizedPath = _normalizedInboxPath(file.path),
       version = SemanticVersion.tryParse(manifest?.version);

  factory _InboxCandidate.valid(
    File file,
    ModManifest manifest,
    String sha256,
  ) => _InboxCandidate._(
    file: file,
    manifest: manifest,
    errors: const [],
    sha256: sha256,
  );

  factory _InboxCandidate.rejected(
    File file,
    ModManifest? manifest,
    List<String> errors, {
    String sha256 = '',
  }) => _InboxCandidate._(
    file: file,
    manifest: manifest,
    errors: errors,
    sha256: sha256,
  );

  final File file;
  final ModManifest? manifest;
  final List<String> errors;
  final String sha256;
  final String normalizedPath;
  final SemanticVersion? version;

  String get fileName => p.basename(file.path);
  bool get isValid => errors.isEmpty && manifest != null && version != null;
  String get groupKey => manifest?.id.trim().isNotEmpty == true
      ? 'id:${_normalizedInboxId(manifest!.id)}'
      : 'path:$normalizedPath';
  LauncherIssue get issue => LauncherIssue(
    severity: IssueSeverity.error,
    subjectId: fileName,
    message:
        'TFINBOX110: $fileName failed safe package preflight and was retained '
        'for inspection. ${errors.isEmpty ? 'Its version is not valid SemVer.' : errors.join(' ')}',
  );
}

class _PendingInboxGroup {
  _PendingInboxGroup(this.selectable);

  final List<_InboxCandidate> selectable;
  Object? lastError;

  _InboxCandidate get winner => selectable.first;
}

int _compareInboxPaths(_InboxCandidate left, _InboxCandidate right) {
  final normalized = left.normalizedPath.compareTo(right.normalizedPath);
  return normalized != 0
      ? normalized
      : left.file.path.compareTo(right.file.path);
}

int _compareInboxSelection(_InboxCandidate left, _InboxCandidate right) {
  final version = right.version!.compareTo(left.version!);
  return version != 0 ? version : _compareInboxPaths(left, right);
}

String _normalizedInboxPath(String path) => unicode
    .nfc(p.normalize(p.absolute(path)).replaceAll('\\', '/'))
    .toLowerCase();

String _normalizedInboxId(String id) => unicode.nfc(id.trim()).toLowerCase();
