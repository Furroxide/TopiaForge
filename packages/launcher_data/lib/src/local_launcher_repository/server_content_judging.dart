part of '../local_launcher_repository.dart';

// How a cleanup and a repair judge what the record of server content
// names: by id and version, by install receipt, and by the server
// profiles that pin it.

ServerContentCleanupResult _cleanupRefused(
  ServerContentCleanupRefusal refusal,
  String message,
) => ServerContentCleanupResult.refused(refusal, message);

String _versionKey(String id, String version) => '${id.toLowerCase()}@$version';

/// Whether [folder] still holds what the record says of it, by its install
/// receipt [receiptSha256]: false once it is gone or was installed again,
/// and null while its receipt cannot be read just now, when nothing is
/// decided about it.
Future<bool?> _judgeRecorded(Directory folder, String receiptSha256) async {
  if (FileSystemEntity.typeSync(folder.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    return false;
  }
  final receipt = await _readReceiptDigest(folder);
  if (receipt.unreadable) return null;
  return receipt.sha256 == receiptSha256;
}

bool _pinsVersion(LauncherProfile server, ServerContentEntry entry) {
  final id = entry.modId.toLowerCase();
  if (!server.enabledMods.any((item) => item.toLowerCase() == id)) {
    return false;
  }
  for (final pin in server.selectedVersions.entries) {
    if (pin.key.toLowerCase() == id) return pin.value == entry.version;
  }
  return false;
}

/// The bytes of the files beneath [folder], links left out.
int _folderBytes(Directory folder) {
  var total = 0;
  try {
    for (final entity in folder.listSync(recursive: true, followLinks: false)) {
      if (entity is File) total += entity.lengthSync();
    }
  } on FileSystemException {
    // A size that cannot be read counts what could.
  }
  return total;
}
