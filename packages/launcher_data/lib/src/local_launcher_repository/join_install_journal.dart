part of '../local_launcher_repository.dart';

/// How far a join's install got before it stopped.
enum _JoinJournalPhase {
  /// Every package is staged; nothing the game reads has changed.
  staged,

  /// It began recording and moving packages into place.
  committing,
}

/// A package the journal names: where it goes and the receipt it carries,
/// which tells this join's folder from any other.
final class _JoinJournalPackage {
  const _JoinJournalPackage({
    required this.id,
    required this.version,
    required this.receiptSha256,
  });

  final String id;
  final String version;
  final String receiptSha256;
}

/// The journal a join's install keeps in its transaction folder, so a
/// launcher that stopped part way can finish or undo it on its next start.
final class _JoinJournal {
  const _JoinJournal({
    required this.phase,
    required this.packages,
    required this.addedRecords,
  });

  static const schema = 'topiaforge.join-install/1';
  static const fileName = 'journal.json';
  static const maximumBytes = 1024 * 1024;

  final _JoinJournalPhase phase;
  final List<_JoinJournalPackage> packages;

  /// The mods the join records, disabled, because they were new to the
  /// player.
  final List<String> addedRecords;

  Map<String, Object?> toJson() => {
    'schema': schema,
    'phase': phase.name,
    'packages': [
      for (final package in packages)
        {
          'id': package.id,
          'version': package.version,
          'receipt': package.receiptSha256,
        },
    ],
    'addedRecords': addedRecords,
  };

  /// Reads a journal, or null when anything in it is not as written: a
  /// journal is only acted on whole.
  static _JoinJournal? tryRead(Object? json) {
    if (json is! Map || json['schema'] != schema) return null;
    final phase = _JoinJournalPhase.values
        .where((item) => item.name == json['phase'])
        .firstOrNull;
    final packages = json['packages'];
    final added = json['addedRecords'];
    if (phase == null || packages is! List || added is! List) return null;
    final read = <_JoinJournalPackage>[];
    for (final item in packages) {
      if (item is! Map) return null;
      final id = item['id'];
      final version = item['version'];
      final receipt = item['receipt'];
      if (id is! String ||
          !ModManifest.isValidId(id) ||
          version is! String ||
          SemanticVersion.tryParse(version) == null ||
          version.contains('/') ||
          version.contains(r'\') ||
          receipt is! String ||
          !_exactSha256Pattern.hasMatch(receipt)) {
        return null;
      }
      read.add(
        _JoinJournalPackage(id: id, version: version, receiptSha256: receipt),
      );
    }
    final ids = <String>[];
    for (final id in added) {
      if (id is! String || !ModManifest.isValidId(id)) return null;
      ids.add(id);
    }
    return _JoinJournal(phase: phase, packages: read, addedRecords: ids);
  }
}

Future<void> _writeJoinJournal(Directory root, _JoinJournal journal) =>
    _writeJsonFileAtomic(
      File(p.join(root.path, _JoinJournal.fileName)),
      journal.toJson(),
      maxBytes: _JoinJournal.maximumBytes,
      label: 'Join install journal',
    );

/// Whether [name] is a folder a join's install or a cleanup leaves in
/// manager staging.
bool _isJoinFolderName(String name) =>
    name.startsWith('launcher-join-') || name.startsWith('launcher-trash-');

extension _JoinInstallJournal on LocalLauncherRepository {
  /// Finishes or undoes what a join's install left when the launcher stopped
  /// part way, unless a launch or an install holds the staging lease just
  /// now. Tidying, so it never throws.
  Future<void> _sweepJoinInstallsWhenFree(GameInstall install) async {
    try {
      final staging = _managerStaging(install);
      if (!staging.existsSync() ||
          !staging
              .listSync(followLinks: false)
              .any((entity) => _isJoinFolderName(p.basename(entity.path)))) {
        return;
      }
      final lease = await LaunchStagingStore(install.path).acquireLaunchLease();
      if (lease == null) return;
      try {
        await _sweepJoinInstalls(install);
      } finally {
        await lease.release();
      }
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Stopped join installs could not be swept: $error',
      );
    }
  }

  /// Under the staging lease: settles each join folder an install or a
  /// cleanup that stopped part way left, and deletes it. A folder that
  /// cannot be settled yet stays for the next start.
  ///
  /// - No journal, or every package only staged: nothing the game reads
  ///   changed, so the folder just goes.
  /// - Committing, with every package in place: the install finished before
  ///   it stopped, so it stays installed.
  /// - Committing, part way: everything the journal names is put back, as
  ///   the install's own rollback would: its folders, its entries in the
  ///   record of server content, and the manager records it added.
  /// - A cleanup's folder holds only content already taken out of the
  ///   packages folder, so it goes.
  Future<void> _sweepJoinInstalls(GameInstall install) async {
    final staging = _managerStaging(install);
    if (!staging.existsSync()) return;
    for (final entity in staging.listSync(followLinks: false)) {
      if (entity is! Directory || !_isJoinFolderName(p.basename(entity.path))) {
        continue;
      }
      try {
        if (await _settleJoinFolder(install, entity)) {
          entity.deleteSync(recursive: true);
        }
      } on Object catch (error) {
        await _appendLauncherLogBestEffort(
          'A stopped join install could not be settled yet: $error',
        );
      }
    }
  }

  Future<bool> _settleJoinFolder(GameInstall install, Directory root) async {
    if (p.basename(root.path).startsWith('launcher-trash-')) return true;
    final journal = await _readJoinJournal(root);
    if (journal == null || journal.phase == _JoinJournalPhase.staged) {
      return true;
    }
    final packagesRoot = _packagesRoot(install);
    final placed = <Directory>[];
    for (final package in journal.packages) {
      final target = Directory(
        p.join(packagesRoot.path, package.id, package.version),
      );
      if (await _receiptSha256(target) == package.receiptSha256) {
        placed.add(target);
      }
    }
    if (placed.length == journal.packages.length) {
      await _appendLauncherLogBestEffort(
        'Kept a join install that finished before the launcher stopped.',
      );
      return true;
    }
    for (final target in placed) {
      target.deleteSync(recursive: true);
      final parent = target.parent;
      if (parent.existsSync() && parent.listSync().isEmpty) parent.deleteSync();
    }
    await _serverContentStore(install).update(
      (record) => record.without([
        for (final entry in record.entries)
          if (journal.packages.any(
            (package) =>
                entry.names(package.id, package.version) &&
                entry.receiptSha256 == package.receiptSha256,
          ))
            entry,
      ], addedRecords: journal.addedRecords),
    );
    await _removeJoinManagerRecords(install, journal.addedRecords);
    await _appendLauncherLogBestEffort(
      'Undid a join install that stopped part way.',
    );
    return true;
  }

  /// Removes the manager records a join added for [ids], as long as each is
  /// still disabled, unpinned and not being uninstalled, and its mod has no
  /// installed version left.
  Future<void> _removeJoinManagerRecords(
    GameInstall install,
    Iterable<String> ids,
  ) async {
    final lower = {for (final id in ids) id.toLowerCase()};
    if (lower.isEmpty) return;
    final state = await _readManagerState(install);
    final mods = state['mods'] as List;
    bool added(Object? item) {
      if (item is! Map) return false;
      final id = item['id'];
      return id is String &&
          lower.contains(id.toLowerCase()) &&
          item['enabled'] == false &&
          item['versionPinned'] != true &&
          item['uninstallPending'] != true &&
          !_hasInstalledVersion(install, id);
    }

    final kept = [
      for (final item in mods)
        if (!added(item)) item,
    ];
    if (kept.length == mods.length) return;
    state['mods'] = kept;
    await _saveManagerState(install, state);
  }

  bool _hasInstalledVersion(GameInstall install, String id) {
    final folder = Directory(p.join(_packagesRoot(install).path, id));
    try {
      return folder.existsSync() &&
          folder.listSync(followLinks: false).whereType<Directory>().isNotEmpty;
    } on FileSystemException {
      return true;
    }
  }

  Future<_JoinJournal?> _readJoinJournal(Directory root) async {
    final file = File(p.join(root.path, _JoinJournal.fileName));
    if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
        FileSystemEntityType.file) {
      return null;
    }
    try {
      return _JoinJournal.tryRead(
        await _readJsonFileBounded(
          file,
          maxBytes: _JoinJournal.maximumBytes,
          label: 'Join install journal',
        ),
      );
    } on Object {
      return null;
    }
  }
}

JoinFailed _nothingInstalled(String detail) => JoinFailed.installFailed(
  rollbackComplete: true,
  detail: '$detail Nothing was installed.',
);

String _failureText(Object error) => switch (error) {
  StateError(:final message) => message,
  FileSystemException(:final message) => message,
  FormatException(:final message) => message,
  _ => '$error',
};

/// Deletes a join's or a cleanup's folder in manager staging; one that
/// stays is swept on a later start.
void _deleteJoinFolder(Directory? folder) {
  if (folder == null) return;
  try {
    if (folder.existsSync()) folder.deleteSync(recursive: true);
  } on FileSystemException {
    // Swept later.
  }
}
