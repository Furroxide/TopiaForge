part of '../local_launcher_repository.dart';

/// A package a join installs once its preflight passed.
final class _PendingJoinPackage {
  _PendingJoinPackage(
    this.item, {
    required this.hidden,
    required this.addRecord,
  });

  final JoinPlanPackage item;

  /// Whether the player's own profiles never see it: the mod was already
  /// the player's ([ServerContentEntry.hidden]).
  final bool hidden;

  /// Whether the mod has neither a manager record nor an installed version,
  /// so the join records it, disabled, before its folder appears.
  final bool addRecord;

  ModManifest get manifest => item.manifest!;
}

/// One package staged in a join's transaction folder.
final class _StagedJoinPackage {
  _StagedJoinPackage(this.pending, this.staged, this.receiptSha256);

  final _PendingJoinPackage pending;
  final _StagedPackageInstall staged;
  final String receiptSha256;

  ServerContentEntry entry(int installedAt) => ServerContentEntry(
    modId: pending.manifest.id,
    version: pending.manifest.version,
    sha256: pending.item.package.sha256,
    receiptSha256: receiptSha256,
    hidden: pending.hidden,
    installedAt: installedAt,
  );
}

/// An install refused before it changed anything.
final class _JoinInstallRefused implements Exception {
  const _JoinInstallRefused(this.message);

  final String message;
}

extension _JoinContentInstall on LocalLauncherRepository {
  Future<JoinEvent?> _installJoinContent(
    GameInstall install,
    JoinSessionState state, {
    void Function(JoinInstallProgressed progress)? onProgress,
    CancellationToken? cancellation,
  }) async {
    final plan = state.plan;
    if (state.stage != JoinStage.installing || plan == null) return null;
    if (plan.hasUnverifiedPackages) {
      return _nothingInstalled(
        'Only the host has some of these mods, and this launcher cannot '
        'install mods from a host yet. Ask the host to publish them to a mod '
        'source you use, then join again.',
      );
    }
    final LaunchStagingLease? lease;
    try {
      lease = await LaunchStagingStore(install.path).acquireLaunchLease();
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Join install could not take the staging lease: $error',
      );
      return _nothingInstalled(
        'The game folder could not be prepared for installing. Check that it '
        'still exists and can be written to, then join again.',
      );
    }
    if (lease == null) {
      return _nothingInstalled(
        'Another launch or install is using the game folder right now. Wait '
        'for it to finish, then join again.',
      );
    }
    try {
      // A damaged record is repaired, failing closed, and a join install that
      // stopped part way is settled, before anything is decided.
      await _repairServerContent(install);
      await _sweepJoinInstalls(install);
      final current = await _validateGameDirectory(install.path);
      final blocking = current.issues.where((issue) => issue.isBlocking);
      if (blocking.isNotEmpty) {
        return _nothingInstalled(
          '${blocking.map((item) => item.message).join(' ')} Fix the game '
          'install in Setup, then join again.',
        );
      }
      return await _JoinInstall(
        this,
        current,
        plan,
        cancellation,
        onProgress,
        closeOwnedGame: state.closeOwnedGameAcknowledged,
      ).run();
    } on _JoinInstallRefused catch (refusal) {
      return _nothingInstalled(refusal.message);
    } on Object catch (error) {
      // Nothing escapes an install once it changed what the game reads.
      await _appendLauncherLogBestEffort(
        'Join install could not start: $error',
      );
      return _nothingInstalled(
        'The install could not start. Check that the game folder can be '
        'written to, then join again.',
      );
    } finally {
      try {
        await lease.release();
      } on Object catch (error) {
        await _appendLauncherLogBestEffort(
          'Join install lease release failed: $error',
        );
      }
    }
  }
}

/// One join's install as one change. It stages every package, then, in
/// this order, records the mods new to the player as disabled, records what
/// it installs in the record of server content, and moves each package into
/// place. Nothing the game reads changes before that order begins, so a stop
/// or a cancel before it undoes everything; a failure after it rolls back
/// everything this join changed, and only that.
///
/// A game this launcher started is closed only when the consent covered it,
/// and only after everything is staged and the last cancel was looked for,
/// so a join that is refused or cancelled leaves the player's game running.
/// What was decided is then decided again, since the game writes its manager
/// state when it quits.
///
/// A journal in the join's transaction folder (`launcher-join-*` in manager
/// staging) says how far it got, so a launcher that stops part way is undone
/// on its next start ([_JoinInstallJournal]).
final class _JoinInstall {
  _JoinInstall(
    this._repository,
    this._install,
    this._plan,
    this._cancellation,
    this._onProgress, {
    required bool closeOwnedGame,
  }) : _closeOwnedGame = closeOwnedGame;

  final LocalLauncherRepository _repository;
  final GameInstall _install;
  final JoinPlan _plan;
  final CancellationToken? _cancellation;
  final void Function(JoinInstallProgressed progress)? _onProgress;
  final bool _closeOwnedGame;
  final _pending = <_PendingJoinPackage>[];
  final _staged = <_StagedJoinPackage>[];

  /// The ids whose manager record this join added.
  final _addedRecords = <String>[];
  Directory? _root;

  bool get _cancelled => _cancellation?.isCancelled ?? false;

  Future<JoinEvent> run() async {
    _pending.addAll(await _decide());
    if (_pending.isEmpty) return const JoinInstallCompleted();
    final blocked = await _repository._gameBlockingJoinInstall(
      _install,
      closeOwnedGame: _closeOwnedGame,
    );
    if (blocked != null) return blocked;
    await _progress(0);
    try {
      for (final pending in _pending) {
        if (_cancelled) return await _undo();
        await _stage(pending);
        await _progress(_staged.length);
      }
      await _writeJournal(_JoinJournalPhase.staged);
      await _hook('staged');
      if (_cancelled) return await _undo();
      final closing = await _repository._closeOwnedGameForInstall(_install);
      if (closing != null) {
        await _discard();
        return closing;
      }
      if (!_decidedAsStaged(await _decide())) {
        await _discard();
        return _nothingInstalled(
          'Your mods in this game changed while the join was preparing. Join '
          'again.',
        );
      }
    } on _JoinInstallRefused {
      await _discard();
      rethrow;
    } on Object catch (error) {
      await _discard();
      await _log('Preparing a join install failed: $error');
      return _nothingInstalled(
        "The server's mods could not be prepared for installing. Check that "
        'there is free disk space and the game folder can be written to, then '
        'join again.',
      );
    }
    try {
      await _writeJournal(_JoinJournalPhase.committing);
      await _recordNewMods();
      await _hook('state');
      await _recordServerContent();
      await _hook('record');
      for (var index = 0; index < _staged.length; index++) {
        _staged[index].staged.commit();
        await _hook('committed ${index + 1}');
      }
    } on Object catch (error) {
      await _log('A join install failed: $error');
      final complete = await _rollBack();
      return JoinFailed.installFailed(
        rollbackComplete: complete,
        detail: complete
            ? "Installing the server's mods failed, and everything was put "
                  'back as it was. Check that there is free disk space and the '
                  'game folder can be written to, then join again.'
            : "Installing the server's mods failed, and not everything could "
                  'be put back yet. Restart the launcher before you play: it '
                  'finishes putting things back when it starts.',
      );
    }
    await _finish();
    await _repository._appendLauncherLogBestEffort(
      'Installed ${_staged.length} package(s) for server profile '
      '${_plan.derivedProfile.id}.',
    );
    return const JoinInstallCompleted();
  }

  Future<void> _stage(_PendingJoinPackage pending) async {
    final item = pending.item;
    final name = _joinModName(item);
    final _PackageReadResult read;
    try {
      read = await _repository._readPackage(
        _repository._joinCacheFile(item.package.sha256).path,
        expectedSha256: item.package.sha256,
      );
    } on Object {
      throw _JoinInstallRefused(
        'The download of $name is missing or damaged. Join again to download '
        'it.',
      );
    }
    if (_canonicalPackageManifest(read.manifest) !=
        _canonicalPackageManifest(pending.manifest)) {
      throw _JoinInstallRefused(
        'The download of $name does not match its listing in your sources. '
        'Join again to download it afresh.',
      );
    }
    final root = _root ??= (_repository._managerStaging(
      _install,
    )..createSync(recursive: true)).createTempSync('launcher-join-');
    final staged = await _repository._stagePackageInstall(
      _PackageReadResult(
        archive: read.archive,
        manifest: read.manifest,
        sha256Hex: read.sha256Hex,
        reference: item.resolution.downloadUrl,
        trust: read.trust,
      ),
      _install,
      source: _packageReceiptSource(
        reference: item.resolution.downloadUrl,
        sourceId: item.resolution.sourceId,
        sourceKind: 'registry',
      ),
      transactionParent: root,
    );
    final receipt = await _receiptSha256(staged.staging);
    if (receipt == null) {
      staged.clean(stateSaved: false);
      throw StateError('The install receipt of $name could not be read.');
    }
    _staged.add(_StagedJoinPackage(pending, staged, receipt));
  }

  Future<void> _writeJournal(_JoinJournalPhase phase) => _writeJoinJournal(
    _root!,
    _JoinJournal(
      phase: phase,
      packages: [
        for (final item in _staged)
          _JoinJournalPackage(
            id: item.pending.manifest.id,
            version: item.pending.manifest.version,
            receiptSha256: item.receiptSha256,
          ),
      ],
      addedRecords: [
        for (final item in _staged)
          if (item.pending.addRecord) item.pending.manifest.id,
      ],
    ),
  );

  /// Records each mod new to the player as installed but disabled, before
  /// its folder appears, so neither the game nor any profile of the
  /// player's own loads it. A mod that already has a record keeps it exactly
  /// as it is: its state is the player's.
  Future<void> _recordNewMods() async {
    final state = await _repository._readManagerState(_install);
    final recorded = _repository._stateByModId(state);
    for (final item in _staged) {
      final manifest = item.pending.manifest;
      if (!item.pending.addRecord ||
          recorded.containsKey(manifest.id.toLowerCase())) {
        continue;
      }
      _repository._upsertState(
        state,
        manifest,
        enabled: false,
        restartRequired: false,
      );
      _addedRecords.add(manifest.id);
    }
    if (_addedRecords.isNotEmpty) {
      await _repository._saveManagerState(_install, state);
    }
  }

  Future<void> _recordServerContent() async {
    final now = DateTime.now().toUtc().millisecondsSinceEpoch ~/ 1000;
    await _repository
        ._serverContentStore(_install)
        .update(
          (record) => record.withInstalled([
            for (final item in _staged) item.entry(now),
          ], addedRecords: _addedRecords),
        );
  }

  /// Puts back everything this join changed, newest first; says whether all
  /// of it went back. What could not stays in the journal for the next
  /// start.
  Future<bool> _rollBack() async {
    var complete = true;
    for (final item in _staged.reversed) {
      try {
        item.staged.rollback();
      } on Object {
        complete = false;
      }
    }
    for (final item in _staged) {
      item.staged.clean(stateSaved: false);
    }
    try {
      final entries = [for (final item in _staged) item.entry(0)];
      await _repository
          ._serverContentStore(_install)
          .update(
            (record) => record.without([
              for (final entry in record.entries)
                if (entries.any(
                  (item) =>
                      entry.names(item.modId, item.version) &&
                      entry.receiptSha256 == item.receiptSha256,
                ))
                  entry,
            ], addedRecords: _addedRecords),
          );
    } on Object {
      complete = false;
    }
    try {
      await _repository._removeJoinManagerRecords(_install, _addedRecords);
    } on Object {
      complete = false;
    }
    if (complete) _deleteJoinFolder(_root);
    return complete;
  }

  Future<JoinEvent> _undo() async {
    await _discard();
    return const JoinInstallUndone();
  }

  /// Drops everything staged; nothing the game reads changed yet.
  Future<void> _discard() async {
    for (final item in _staged) {
      item.staged.clean(stateSaved: false);
    }
    _deleteJoinFolder(_root);
  }

  Future<void> _finish() async {
    for (final item in _staged) {
      item.staged.clean(stateSaved: true);
    }
    _deleteJoinFolder(_root);
  }

  Future<void> _hook(String step) async {
    final hook = _repository._joinInstallHook;
    if (hook != null) await hook(step);
  }

  /// Tells the listener how many packages are prepared. Progress is only
  /// shown, so a listener that throws is logged and the install goes on.
  Future<void> _progress(int done) async {
    final report = _onProgress;
    if (report == null) return;
    try {
      report(
        JoinInstallProgressed(
          packagesDone: done,
          packagesTotal: _pending.length,
        ),
      );
    } on Object catch (error) {
      await _log('A join install progress listener failed: $error');
    }
  }

  Future<void> _log(String message) =>
      _repository._appendLauncherLogBestEffort(message);
}
