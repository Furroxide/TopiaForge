part of '../local_launcher_repository.dart';

/// The installed versions a selection that is not a server profile's must
/// not see: what the record of server content withholds
/// ([ServerContentRecord.withholds]), decided as the game's loader decides
/// it, so the launcher and a game started without it agree.
///
/// So a join never changes what the player's own profiles load, even when it
/// installs a newer version of a mod they leave unpinned. Server profiles pin
/// every package and see every version. A mod new to the player stays
/// visible; its manager record starts disabled, so nothing loads it unless
/// the player turns it on, and it starts disabled again if that record is
/// lost, as the game's loader starts it ([records]).
///
/// While the record cannot be used, the view freezes as the game's loader
/// freezes its selection: each mod offers only the version its manager
/// record names, and a mod with no manager record offers nothing, so no view
/// of the player's own play ever shows a version the record may withhold.
final class _HiddenServerVersions {
  _HiddenServerVersions(this._record) : _named = null, _exactPins = false;

  _HiddenServerVersions.frozen(Map<String, Map<dynamic, dynamic>> stateById)
    : _record = ServerContentRecord.empty(),
      _exactPins = false,
      _named = {
        for (final entry in stateById.entries)
          if (entry.value['version'] case final String version)
            entry.key: version,
      };

  _HiddenServerVersions._pinned(this._record)
    : _named = null,
      _exactPins = true;

  final ServerContentRecord _record;

  /// While the record cannot be used: the version each mod's manager record
  /// names, by lowercase id.
  final Map<String, String>? _named;
  final bool _exactPins;

  /// Whether the record could be used. While it cannot, a launch of a
  /// profile of the player's own waits for it.
  bool get usable => _named == null;

  /// What an exact pin of a profile of the player's own sees, when that is
  /// more: a version a repair withheld stays out of unpinned selection only,
  /// as the game's loader loads every exact pin, while a version a join hid
  /// stays the server's. Null when exact pins see what unpinned selection
  /// sees.
  _HiddenServerVersions? get forExactPins =>
      usable && !_exactPins && _record.withheld.isNotEmpty
      ? _HiddenServerVersions._pinned(_record)
      : null;

  /// [stateById] with a disabled record for each mod a join added whose
  /// manager record is gone, so that it starts disabled, as the game's loader
  /// starts it.
  Map<String, Map<dynamic, dynamic>> records(
    Map<String, Map<dynamic, dynamic>> stateById,
  ) => _record.addedRecords.every(stateById.containsKey)
      ? stateById
      : {
          for (final id in _record.addedRecords)
            id: <dynamic, dynamic>{'enabled': false},
          ...stateById,
        };

  /// Whether [versionDirectory], a version folder in the mod folder
  /// [idDirectory], is hidden. A version the record names whose receipt
  /// cannot be read is hidden, failing closed.
  Future<bool> hides(Directory idDirectory, Directory versionDirectory) async {
    final id = p.basename(idDirectory.path);
    final version = p.basename(versionDirectory.path);
    final named = _named;
    if (named != null) return named[id.toLowerCase()] != version;
    if (_exactPins) {
      final entry = _record.entryFor(id, version);
      if (entry == null || !entry.hidden) return false;
      final receipt = (await _readReceiptDigest(versionDirectory)).sha256;
      return receipt == null || receipt == entry.receiptSha256;
    }
    if (!_record.withholds(id, version, null)) return false;
    return _record.withholds(
      id,
      version,
      (await _readReceiptDigest(versionDirectory)).sha256,
    );
  }
}

/// A version folder's install receipt: its SHA-256, or null when there is
/// none, and whether one is there but could not be read just now, as when
/// another program holds it.
typedef _ReceiptDigest = ({String? sha256, bool unreadable});

/// Reads [packageDirectory]'s install receipt ([_ReceiptDigest]). A version
/// whose receipt cannot be read is never judged by it: nothing removes it,
/// and the record keeps what it says about it.
Future<_ReceiptDigest> _readReceiptDigest(Directory packageDirectory) async {
  final file = File(
    p.join(packageDirectory.path, _packageInstallReceiptFileName),
  );
  try {
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type != FileSystemEntityType.file) {
      return (sha256: null, unreadable: false);
    }
    return (
      sha256: sha256
          .convert(
            await _readLauncherFileBounded(file, _maxInstalledReceiptBytes),
          )
          .toString(),
      unreadable: false,
    );
  } on Object {
    return (sha256: null, unreadable: true);
  }
}

/// The SHA-256 of [packageDirectory]'s install receipt, or null when it has
/// none that can be read.
Future<String?> _receiptSha256(Directory packageDirectory) async =>
    (await _readReceiptDigest(packageDirectory)).sha256;

/// The SHA-256 of the archive [packageDirectory]'s install receipt names, or
/// null when the receipt cannot be read.
Future<String?> _receiptArchiveSha256(Directory packageDirectory) async {
  try {
    final decoded = jsonDecode(
      utf8.decode(
        await _readLauncherFileBounded(
          File(p.join(packageDirectory.path, _packageInstallReceiptFileName)),
          _maxInstalledReceiptBytes,
        ),
      ),
    );
    final value = decoded is Map ? decoded['sourceSha256'] : null;
    return value is String && _exactSha256Pattern.hasMatch(value)
        ? value
        : null;
  } on Object {
    return null;
  }
}

extension _ServerContentAccess on LocalLauncherRepository {
  ServerContentStore _serverContentStore(GameInstall install) {
    final directory = p.normalize(p.absolute(_managerRoot(install).path));
    return _serverContentStores.putIfAbsent(
      Platform.isWindows ? directory.toLowerCase() : directory,
      () => ServerContentStore(directory),
    );
  }

  /// What a selection that is not a server profile's must not see in
  /// [install], whose manager records are [stateById]: frozen at what they
  /// name while the record of server content cannot be used.
  Future<_HiddenServerVersions> _hiddenServerVersions(
    GameInstall install,
    Map<String, Map<dynamic, dynamic>> stateById,
  ) async {
    final load = await _serverContentStore(install).load();
    return load.status.isUsable
        ? _HiddenServerVersions(load.record)
        : _HiddenServerVersions.frozen(stateById);
  }

  /// Why a launch of a profile of the player's own is blocked while the
  /// record of server content cannot be used, and what to do.
  Future<LauncherIssue> _serverContentIssue(GameInstall install) async {
    final status = (await _serverContentStore(install).load()).status;
    return LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: 'server-content',
      message: _serverContentUnusable(status, 'launch'),
    );
  }

  /// Repairs a damaged record of server content in [install], unless a
  /// launch or install holds the staging lease just now. Tidying, so it
  /// never throws.
  Future<void> _repairServerContentWhenFree(GameInstall install) async {
    try {
      final load = await _serverContentStore(install).load();
      if (load.status != ServerContentStatus.damaged) return;
      final lease = await LaunchStagingStore(install.path).acquireLaunchLease();
      if (lease == null) return;
      try {
        await _repairServerContent(install);
      } finally {
        await lease.release();
      }
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'The record of server content could not be repaired: $error',
      );
    }
  }

  /// Under the staging lease: replaces a damaged record of server content
  /// with one that fails closed. It keeps every entry that still reads, and
  /// withholds each other installed version the manager state does not name,
  /// so no version a join hid can become the player's own. A pin of a
  /// profile of the player's own is no evidence, since a copy of a server
  /// profile pins the server's versions; an exact pin still loads a withheld
  /// version ([_HiddenServerVersions.forExactPins]). Returns the record as it
  /// then stands.
  Future<ServerContentLoad> _repairServerContent(GameInstall install) async {
    final store = _serverContentStore(install);
    final before = await store.load();
    if (before.status != ServerContentStatus.damaged) return before;
    final repaired = await store.repair(
      (salvaged) => _rebuildServerContent(install, salvaged),
    );
    await _appendLauncherLogBestEffort(
      repaired.status == ServerContentStatus.valid
          ? 'Repaired the damaged record of server content, withholding '
                '${repaired.record.withheld.length} installed version(s) from '
                'the player\'s own profiles. A copy of it is kept aside.'
          : 'The damaged record of server content was not repaired: '
                '${repaired.problem}',
    );
    return repaired;
  }

  Future<ServerContentRecord> _rebuildServerContent(
    GameInstall install,
    ServerContentRecord salvaged,
  ) async {
    final named = <String>{
      for (final entry in _stateByModId(
        await _readManagerState(install),
      ).entries)
        if (entry.value['version'] case final String version)
          _versionKey(entry.key, version),
    };
    final withheld = <WithheldServerVersion>[];
    final packagesRoot = _packagesRoot(install);
    if (packagesRoot.existsSync()) {
      for (final idFolder in packagesRoot.listSync(followLinks: false)) {
        if (idFolder is! Directory) continue;
        final id = p.basename(idFolder.path);
        for (final versionFolder in idFolder.listSync(followLinks: false)) {
          if (versionFolder is! Directory) continue;
          final version = p.basename(versionFolder.path);
          if (!ModManifest.isValidId(id) ||
              SemanticVersion.tryParse(version) == null ||
              named.contains(_versionKey(id, version)) ||
              salvaged.entryFor(id, version) != null) {
            continue;
          }
          final receipt = await _readReceiptDigest(versionFolder);
          if (receipt.unreadable) {
            throw StateError('An install receipt could not be read just now.');
          }
          final digest = receipt.sha256;
          // A version with no receipt loads for no one.
          if (digest == null) continue;
          try {
            withheld.add(
              WithheldServerVersion(
                modId: id,
                version: version,
                receiptSha256: digest,
              ),
            );
          } on ArgumentError {
            // Not an id and version a server descriptor can name, so no join
            // installed it.
          }
        }
      }
    }
    return salvaged.withWithheld(withheld);
  }
}

/// What a player is told when the record of server content cannot be used
/// for [action] (`launch`, `join` or `cleanup`), by [status].
String _serverContentUnusable(ServerContentStatus status, String action) {
  final next = switch (action) {
    'launch' => 'then try again',
    'join' => 'then join again',
    _ => 'then clean up again',
  };
  return switch (status) {
    ServerContentStatus.newer =>
      'A newer TopiaForge launcher recorded the mods it installed for servers '
          'in this game, so this launcher cannot tell them from yours. Update '
          'the launcher, $next.',
    ServerContentStatus.damaged =>
      'The record of mods installed for servers in this game is damaged and '
          'could not be repaired just now. Close any program using the game '
          'folder and restart the launcher, which repairs it, $next.',
    _ =>
      'The record of mods installed for servers could not be read just now. '
          'Close any program using BepInEx/TopiaForge/server-content.json, '
          '$next.',
  };
}
