import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'launcher_update_http.dart';
import 'launcher_update_installation.dart';
import 'launcher_update_transaction.dart';
import 'launcher_update_trust.dart';
import 'safe_zip_archive.dart';

part 'local_launcher_update_repository/github_models.dart';
part 'local_launcher_update_repository/repository_helpers.dart';
part 'local_launcher_update_repository/staged_plan_validation.dart';

final class LocalLauncherUpdateRepository implements LauncherUpdateRepository {
  LocalLauncherUpdateRepository({
    required String dataRoot,
    LauncherUpdateTransport? transport,
    LauncherUpdateTrustStore? trustStore,
    LauncherInstallationLayout? installation,
    DateTime Function()? clock,
    this.cooldown = const Duration(hours: 6),
  }) : _dataRoot = Directory(p.normalize(p.absolute(dataRoot))),
       _transport = transport ?? SecureLauncherUpdateTransport(),
       _trustStore = trustStore ?? LauncherUpdateTrustStore.embedded(),
       _installation = installation ?? LauncherInstallationLayout.detect(),
       _clock = clock ?? DateTime.now;

  static final releasesApi = Uri.https(
    'api.github.com',
    '/repos/furroxide/TopiaForge/releases',
    const {'per_page': '20'},
  );

  final Directory _dataRoot;
  final LauncherUpdateTransport _transport;
  final LauncherUpdateTrustStore _trustStore;
  final LauncherInstallationLayout? _installation;
  final DateTime Function() _clock;
  final Duration cooldown;
  final StreamController<LauncherUpdateStatus> _statuses =
      StreamController<LauncherUpdateStatus>.broadcast();
  bool _disposed = false;

  Directory get _updatesRoot => Directory(p.join(_dataRoot.path, 'updates'));
  File get _stateFile => File(p.join(_updatesRoot.path, 'state.json'));
  Directory get _transactionsRoot =>
      Directory(p.join(_updatesRoot.path, 'transactions'));

  @override
  Stream<LauncherUpdateStatus> get statuses => _statuses.stream;

  @override
  Future<LauncherUpdateStatus> checkForUpdate({
    required String currentVersion,
    required LauncherUpdateChannel channel,
    bool force = false,
  }) async {
    _ensureActive();
    if (channel == LauncherUpdateChannel.nightly) {
      return _emit(
        const LauncherUpdateStatus(
          phase: LauncherUpdatePhase.failed,
          message: 'Nightly launcher updates are not published.',
        ),
      );
    }
    final persisted = _readState();
    final lastChecked = DateTime.tryParse(
      persisted['lastCheckedUtc'] as String? ?? '',
    );
    if (!force &&
        lastChecked != null &&
        _clock().toUtc().difference(lastChecked.toUtc()) < cooldown) {
      return _emit(
        const LauncherUpdateStatus(
          phase: LauncherUpdatePhase.current,
          message: 'Launcher updates were checked recently.',
        ),
      );
    }

    _emit(
      const LauncherUpdateStatus(
        phase: LauncherUpdatePhase.checking,
        message: 'Checking signed GitHub release metadata.',
      ),
    );
    try {
      _ensureLauncherUpdateStorage(_dataRoot, _updatesRoot, _transactionsRoot);
      final releasesBytes = await _transport.fetch(
        releasesApi,
        maxBytes: 2 * 1024 * 1024,
        label: 'GitHub releases',
      );
      final releases = _decodeReleaseList(releasesBytes);
      releases.sort((left, right) {
        final leftVersion = SemanticVersion.tryParse(left.version);
        final rightVersion = SemanticVersion.tryParse(right.version);
        if (leftVersion == null) return 1;
        if (rightVersion == null) return -1;
        return rightVersion.compareTo(leftVersion);
      });
      LauncherUpdateCandidate? available;
      for (final release in releases) {
        if (release.draft ||
            (channel == LauncherUpdateChannel.release && release.prerelease)) {
          continue;
        }
        final target = SemanticVersion.tryParse(release.version);
        final current = SemanticVersion.tryParse(currentVersion);
        if (target == null ||
            current == null ||
            target.compareTo(current) <= 0) {
          continue;
        }
        final candidate = await _verifiedCandidate(release);
        if (!candidate.isEligibleFor(
          currentVersion: currentVersion,
          requestedChannel: channel,
        )) {
          continue;
        }
        _rejectReplay(candidate, persisted);
        available = candidate;
        break;
      }
      _writeState({
        ...persisted,
        'lastCheckedUtc': _clock().toUtc().toIso8601String(),
        if (available != null) 'highestSeenVersion': available.version,
      });
      if (available == null) {
        return _emit(
          const LauncherUpdateStatus(
            phase: LauncherUpdatePhase.current,
            message: 'TopiaForge is up to date.',
          ),
        );
      }
      return _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.available,
          candidate: available,
          message:
              'TopiaForge ${available.version} is available and signed by '
              '${available.signingKeyId}.',
        ),
      );
    } on Object catch (error) {
      return _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.failed,
          message: 'Update check failed: $error',
        ),
      );
    }
  }

  @override
  Future<LauncherUpdateStatus> stageUpdate(
    LauncherUpdateCandidate candidate,
  ) async {
    _ensureActive();
    final layout = _installation;
    if (layout == null) {
      return _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.failed,
          candidate: candidate,
          message:
              'This installation layout cannot be updated in place. Use the '
              'verified manual download at ${candidate.releaseUrl}.',
        ),
      );
    }
    Directory? transaction;
    Directory? extractionRoot;
    try {
      _ensureLauncherUpdateStorage(_dataRoot, _updatesRoot, _transactionsRoot);
      layout.validateCurrent();
      final artifact = candidate.platforms[layout.platformId];
      if (artifact == null || artifact.installLayout != layout.installLayout) {
        throw StateError('No signed update matches this installation.');
      }
      final transactionId = _randomHex(16);
      final transactionDirectory = Directory(
        p.join(_transactionsRoot.path, transactionId),
      );
      if (FileSystemEntity.typeSync(
            transactionDirectory.path,
            followLinks: false,
          ) !=
          FileSystemEntityType.notFound) {
        throw StateError('Update transaction path already exists.');
      }
      transactionDirectory.createSync();
      transaction = transactionDirectory;
      final stagingRoot = Directory(
        p.join(
          p.dirname(layout.targetRoot),
          '.topiaforge-update-$transactionId.staged',
        ),
      );
      extractionRoot = stagingRoot;
      if (FileSystemEntity.typeSync(stagingRoot.path, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError('Update staging path already exists.');
      }
      _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.downloading,
          candidate: candidate,
          message: 'Downloading ${artifact.assetName}.',
        ),
      );
      final partial = File(
        p.join(transactionDirectory.path, '${artifact.assetName}.partial'),
      );
      await _transport.download(
        Uri.parse(artifact.url),
        partialFile: partial,
        expectedSize: artifact.size,
        expectedSha256: artifact.sha256,
        onProgress: (progress) => _emit(
          LauncherUpdateStatus(
            phase: LauncherUpdatePhase.downloading,
            candidate: candidate,
            progress: progress,
            message: 'Downloading ${artifact.assetName}.',
          ),
        ),
      );
      final bytes = partial.readAsBytesSync();
      final archive = SafeZipArchive.decode(
        bytes,
        policy: SafeArchivePolicy(
          maxArchiveBytes: artifact.size,
          maxEntries: artifact.entryCount,
          maxEntryBytes: 512 * 1024 * 1024,
          maxExpandedBytes: artifact.expandedSize,
        ),
        label: 'Signed launcher update',
        allowContainedLinks: layout.installLayout == 'app-bundle',
      );
      final expanded = archive.entries.fold<int>(
        0,
        (total, entry) => total + entry.size,
      );
      if (archive.entries.length != artifact.entryCount ||
          expanded != artifact.expandedSize) {
        throw StateError(
          'Launcher update archive inventory does not match its signature.',
        );
      }
      archive.extractTo(stagingRoot, preserveExecutableMode: true);
      layout.validateStaged(stagingRoot);

      final plan = LauncherUpdateTransactionPlan(
        transactionId: transactionId,
        platformId: layout.platformId,
        targetRoot: layout.targetRoot,
        stagedRoot: layout.stagedRootFrom(stagingRoot),
        backupRoot: p.join(
          p.dirname(layout.targetRoot),
          '.topiaforge-backup-$transactionId',
        ),
        failedRoot: p.join(
          p.dirname(layout.targetRoot),
          '.topiaforge-failed-$transactionId',
        ),
        launcherRelativePath: layout.launcherRelativePath,
        launcherPid: pid,
        healthNonce: _randomHex(32),
        healthFile: p.join(transactionDirectory.path, 'health.json'),
        journalFile: p.join(transactionDirectory.path, 'journal.json'),
        healthTimeoutSeconds: 45,
      );
      final planFile = File(p.join(transactionDirectory.path, 'plan.json'));
      plan.write(planFile);
      const _UpdateJournalSeed().write(plan);
      return _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.staged,
          candidate: candidate,
          progress: 1,
          stagedPlanPath: planFile.path,
          message:
              'The complete signed package is staged. Restart to install it.',
        ),
      );
    } on Object catch (error) {
      _deleteFailedStaging(extractionRoot);
      _deleteFailedStaging(transaction);
      return _emit(
        LauncherUpdateStatus(
          phase: LauncherUpdatePhase.failed,
          candidate: candidate,
          message:
              'Update staging failed: $error. Use the verified manual '
              'download at ${candidate.releaseUrl}.',
        ),
      );
    }
  }

  @override
  Future<void> applyStagedUpdate(LauncherUpdateStatus staged) async {
    _ensureActive();
    if (staged.phase != LauncherUpdatePhase.staged ||
        staged.stagedPlanPath.isEmpty) {
      throw StateError('No verified launcher update is staged.');
    }
    final layout = _installation;
    if (layout == null) {
      throw StateError('This launcher layout cannot apply updates.');
    }
    final planFile = File(staged.stagedPlanPath);
    final plan = LauncherUpdateTransactionPlan.read(planFile);
    _validateLauncherStagedPlan(planFile, plan, layout, _transactionsRoot, pid);
    final helperRoot = Directory(p.join(planFile.parent.path, 'helper'));
    if (FileSystemEntity.typeSync(helperRoot.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError('The external update helper path already exists.');
    }
    helperRoot.createSync();
    if (FileSystemEntity.typeSync(helperRoot.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('The external update helper directory is unsafe.');
    }
    for (final sourcePath in layout.helperSourcePaths) {
      final name = sourcePath == layout.helperSourcePaths.first
          ? layout.helperExecutableName
          : p.basename(sourcePath);
      final destination = File(p.join(helperRoot.path, name));
      File(sourcePath).copySync(destination.path);
      if (!Platform.isWindows) {
        final result = Process.runSync('/bin/chmod', ['755', destination.path]);
        if (result.exitCode != 0) {
          throw StateError('Could not prepare the external update helper.');
        }
      }
    }
    final helper = p.join(helperRoot.path, layout.helperExecutableName);
    await Process.start(
      helper,
      ['launcher', 'apply-update', '--plan', planFile.path],
      workingDirectory: helperRoot.path,
      mode: ProcessStartMode.detached,
    );
    _emit(
      staged.copyWith(
        phase: LauncherUpdatePhase.applying,
        message: 'Restarting into the verified launcher update.',
      ),
    );
  }

  @override
  Future<void> recoverPendingUpdate() async {
    final type = FileSystemEntity.typeSync(
      _transactionsRoot.path,
      followLinks: false,
    );
    if (type == FileSystemEntityType.notFound) return;
    if (type != FileSystemEntityType.directory) {
      throw StateError('Launcher update transaction storage is unsafe.');
    }
    for (final entity in _transactionsRoot.listSync(followLinks: false)) {
      if (entity is! Directory) continue;
      final plan = File(p.join(entity.path, 'plan.json'));
      if (!plan.existsSync()) continue;
      try {
        await const LauncherUpdateTransactionHelper().recover(plan.path);
      } on Object catch (error) {
        _emit(
          LauncherUpdateStatus(
            phase: LauncherUpdatePhase.failed,
            message: 'Update recovery needs attention: $error',
          ),
        );
      }
    }
  }

  LauncherUpdateStatus _emit(LauncherUpdateStatus status) {
    if (!_statuses.isClosed) _statuses.add(status);
    return status;
  }

  void _ensureActive() {
    if (_disposed) throw StateError('Launcher update repository is disposed.');
  }

  @override
  Future<void> dispose() async {
    if (_disposed) return;
    _disposed = true;
    _transport.close();
    await _statuses.close();
  }
}
