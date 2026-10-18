part of '../local_launcher_repository.dart';

/// The installed versions a selection that is not a server profile's must
/// not see: what joins installed for mods the player already had, while each
/// one's install receipt is still the one its join wrote.
///
/// So a join never changes what the player's own profiles load, even when it
/// installs a newer version of a mod they leave unpinned. Server profiles pin
/// every package and see every version. A mod new to the player stays
/// visible; its manager record starts disabled, so nothing loads it unless
/// the player turns it on.
final class _HiddenServerVersions {
  _HiddenServerVersions(Iterable<ServerContentEntry> entries)
    : _receipts = {
        for (final entry in entries)
          if (entry.hidden)
            _key(entry.modId, entry.version): entry.receiptSha256,
      };

  final Map<String, String> _receipts;

  static String _key(String id, String version) =>
      '${id.toLowerCase()}@$version';

  /// Whether [versionDirectory], a version folder in the mod folder
  /// [idDirectory], is hidden.
  Future<bool> hides(Directory idDirectory, Directory versionDirectory) {
    final receipt =
        _receipts[_key(
          p.basename(idDirectory.path),
          p.basename(versionDirectory.path),
        )];
    if (receipt == null) return Future.value(false);
    return _receiptSha256(versionDirectory).then((actual) => actual == receipt);
  }
}

/// The SHA-256 of [packageDirectory]'s install receipt, or null when it has
/// none that can be read.
Future<String?> _receiptSha256(Directory packageDirectory) async {
  try {
    return sha256
        .convert(
          await _readLauncherFileBounded(
            File(p.join(packageDirectory.path, _packageInstallReceiptFileName)),
            _maxInstalledReceiptBytes,
          ),
        )
        .toString();
  } on Object {
    return null;
  }
}

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
  /// [install]; null while the record of server content cannot be read.
  Future<_HiddenServerVersions?> _hiddenServerVersions(
    GameInstall install,
  ) async {
    final load = await _serverContentStore(install).load();
    return load.recovery == MultiplayerStoreRecovery.unreadable
        ? null
        : _HiddenServerVersions(load.record.entries);
  }
}

const _serverContentUnreadable = LauncherIssue(
  severity: IssueSeverity.error,
  subjectId: 'server-content',
  message:
      'The record of mods installed for servers could not be read just now, '
      'so this launch could load a version only a server needs. Close any '
      'program using BepInEx/TopiaForge/server-content.json, then try again.',
);
