part of 'multiplayer_admission.dart';

final _admissionSha256Pattern = RegExp(r'^[A-Fa-f0-9]{64}$');
const _multiplayerContractLockPath = 'topiaforge.multiplayer.lock.json';

bool _validateProfileProtocol(
  MultiplayerAdmissionProfile profile,
  String side,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final versionIsValid =
      SemanticVersion.tryParse(profile.topiaForgeProtocolVersion) != null;
  var rangeIsValid = true;
  if (profile.topiaForgePeerVersionRangeIsPresent) {
    try {
      VersionRange.parse(profile.topiaForgePeerVersionRange);
    } on FormatException {
      rangeIsValid = false;
    }
  }
  if (versionIsValid && rangeIsValid) return true;

  _addInvalid(
    side,
    '',
    'The $side profile has invalid TopiaForge multiplayer protocol metadata.',
    _protocolDisplay(
      profile.topiaForgeProtocolVersion,
      profile.topiaForgePeerVersionRange,
      profile.topiaForgePeerVersionRangeIsPresent,
    ),
    mismatches,
  );
  return false;
}

Map<String, MultiplayerAdmissionMod> _indexAdmissionProfile(
  MultiplayerAdmissionProfile profile,
  String side,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final candidates = <String, List<MultiplayerAdmissionMod>>{};
  for (final item in profile.mods) {
    final rawId = item.manifest.id;
    final id = rawId.trim().toLowerCase();
    if (!ModManifest.isValidId(rawId)) {
      _addInvalid(
        side,
        id,
        'The $side profile contains an invalid mod id.',
        rawId,
        mismatches,
      );
      continue;
    }
    candidates.putIfAbsent(id, () => []).add(item);
  }

  final result = <String, MultiplayerAdmissionMod>{};
  final ids = candidates.keys.toList()..sort();
  for (final id in ids) {
    final entries = candidates[id]!;
    if (entries.length != 1) {
      _addInvalid(
        side,
        id,
        'The $side profile contains duplicate normalized mod ids.',
        id,
        mismatches,
      );
      continue;
    }
    final item = entries.single;
    if (_validateManifestForAdmission(item.manifest, id, side, mismatches)) {
      result[id] = item;
    }
  }
  return result;
}

bool _validateManifestForAdmission(
  ModManifest manifest,
  String id,
  String side,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final errors = <String>[];
  if (manifest.schemaVersion != ModManifest.manifestV5SchemaVersion) {
    errors.add('schemaVersion must be 5');
  } else if (SemanticVersion.tryParse(manifest.version) == null) {
    errors.add('package version must be an exact semantic version');
  }
  final multiplayer = manifest.multiplayer;
  if (errors.isEmpty) {
    if (manifest.multiplayerIsPresent && multiplayer == null) {
      errors.add('multiplayer metadata must be an object');
    } else if (multiplayer != null) {
      _validateMultiplayerMetadata(manifest, multiplayer, errors);
    }
  }

  final orderedErrors = errors.toSet().toList()..sort();
  for (final error in orderedErrors) {
    _addInvalid(
      side,
      id,
      'The $side manifest is invalid for multiplayer admission: $error.',
      error,
      mismatches,
    );
  }
  return errors.isEmpty;
}

void _validateMultiplayerMetadata(
  ModManifest manifest,
  ModMultiplayerMetadata multiplayer,
  List<String> errors,
) {
  final mode = multiplayer.mode;
  if (mode == null) {
    errors.add(
      'multiplayer.mode must be client-local, server-only, or session',
    );
    return;
  }
  if (mode != ModMultiplayerMode.session) {
    if (multiplayer.presenceIsPresent ||
        multiplayer.presenceName != null ||
        multiplayer.protocolIsPresent ||
        multiplayer.protocol != null ||
        multiplayer.synchronizedFilesIsPresent ||
        multiplayer.synchronizedFiles.isNotEmpty) {
      errors.add(
        'non-session modes cannot declare presence, protocol, or synchronized files',
      );
    }
    return;
  }

  if (multiplayer.presence == null) {
    errors.add('session presence must be required or optional');
  }
  final protocol = multiplayer.protocol;
  if (protocol == null) {
    errors.add('session protocol is required');
  } else {
    if (SemanticVersion.tryParse(protocol.version) == null) {
      errors.add('session protocol version must be an exact semantic version');
    }
    final hasRange = _hasDeclaredPeerRange(protocol);
    if (hasRange) {
      final range = protocol.peerVersionRange;
      if (range == null || range.trim().isEmpty) {
        errors.add('session protocol peer range is invalid');
      } else {
        try {
          VersionRange.parse(range);
        } on FormatException {
          errors.add('session protocol peer range is invalid');
        }
      }
    }
  }

  if (multiplayer.synchronizedFiles.length > 256) {
    errors.add('synchronized files exceed the bounded entry limit');
  }
  if (!multiplayer.synchronizedFiles.contains(_multiplayerContractLockPath)) {
    errors.add(
      'session synchronized files must include the canonical generated multiplayer contract lock',
    );
  }
  final collisionKeys = <String>{};
  for (final path in multiplayer.synchronizedFiles) {
    final collisionKey = _admissionPathCollisionKey(path);
    if (collisionKey == null) {
      errors.add(
        'synchronized file paths must be safe portable relative paths',
      );
      continue;
    }
    if (!collisionKeys.add(collisionKey)) {
      errors.add(
        'synchronized file paths must not contain portable collisions',
      );
    }
    final hash = manifest.hashes[path];
    if (hash == null || !_admissionSha256Pattern.hasMatch(hash)) {
      errors.add('every synchronized file must have a packed SHA-256 hash');
    }
  }
}

void _rejectStandaloneOnly(
  Map<String, MultiplayerAdmissionMod> mods,
  String side,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  for (final entry in mods.entries.where(
    (entry) => entry.value.manifest.multiplayer == null,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.standaloneOnlyMod,
        message:
            'The $side enables a standalone-only mod. Disable it in an explicitly confirmed derived profile or add multiplayer metadata.',
        modId: entry.key,
      ),
    );
  }
}

void _rejectWrongSideMods(
  Map<String, MultiplayerAdmissionMod> serverMods,
  Map<String, MultiplayerAdmissionMod> clientMods,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  for (final entry in serverMods.entries.where(
    (entry) =>
        entry.value.manifest.multiplayer?.mode ==
        ModMultiplayerMode.clientLocal,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.clientLocalModOnServer,
        message: 'A client-local mod is enabled in the logical server profile.',
        modId: entry.key,
        serverValue: ModMultiplayerMode.clientLocal.wireName,
        clientValue: 'absent',
      ),
    );
  }
  for (final entry in clientMods.entries.where(
    (entry) =>
        entry.value.manifest.multiplayer?.mode == ModMultiplayerMode.serverOnly,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.serverOnlyModOnClient,
        message:
            'A server-only mod is enabled in the interactive client profile.',
        modId: entry.key,
        serverValue: 'absent',
        clientValue: ModMultiplayerMode.serverOnly.wireName,
      ),
    );
  }
}

MultiplayerAdmissionMismatch _missing(
  String id,
  String message,
  String serverValue,
  String clientValue,
) => MultiplayerAdmissionMismatch(
  code: MultiplayerAdmissionMismatchCode.missingRequiredMod,
  message: message,
  modId: id,
  serverValue: serverValue,
  clientValue: clientValue,
);

void _compareProtocol(
  String id,
  ModMultiplayerProtocol server,
  ModMultiplayerProtocol client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final serverHasRange = _hasDeclaredPeerRange(server);
  final clientHasRange = _hasDeclaredPeerRange(client);
  final serverRange = serverHasRange
      ? server.peerVersionRange!
      : server.version;
  final clientRange = clientHasRange
      ? client.peerVersionRange!
      : client.version;
  if (_mutuallyCompatible(
    server.version,
    serverRange,
    serverHasRange,
    client.version,
    clientRange,
    clientHasRange,
  )) {
    return;
  }
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.modProtocolMismatch,
      message: 'The mod protocol ranges are not mutually compatible.',
      modId: id,
      serverValue: _protocolDisplay(
        server.version,
        serverRange,
        serverHasRange,
      ),
      clientValue: _protocolDisplay(
        client.version,
        clientRange,
        clientHasRange,
      ),
    ),
  );
}

void _compareContent(
  String id,
  ModManifest server,
  ModManifest client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final serverPaths = [...server.multiplayer!.synchronizedFiles]..sort();
  final clientPaths = [...client.multiplayer!.synchronizedFiles]..sort();
  if (!_equalStrings(serverPaths, clientPaths)) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
        message: 'The synchronized-file inventories differ.',
        modId: id,
        serverValue: serverPaths.join(','),
        clientValue: clientPaths.join(','),
      ),
    );
    return;
  }
  for (final path in serverPaths) {
    final serverHash = server.hashes[path] ?? '';
    final clientHash = client.hashes[path] ?? '';
    if (serverHash.toLowerCase() != clientHash.toLowerCase()) {
      mismatches.add(
        MultiplayerAdmissionMismatch(
          code: MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
          message: "Synchronized content differs at '$path'.",
          modId: id,
          serverValue: serverHash,
          clientValue: clientHash,
        ),
      );
    }
  }
}

void _compareExactProfile(
  String id,
  MultiplayerAdmissionMod server,
  MultiplayerAdmissionMod client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  if (server.manifest.version == client.manifest.version &&
      _admissionSha256Pattern.hasMatch(server.packageSha256) &&
      _admissionSha256Pattern.hasMatch(client.packageSha256) &&
      server.packageSha256.toLowerCase() ==
          client.packageSha256.toLowerCase()) {
    return;
  }
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.exactProfileMismatch,
      message:
          'The exact-profile policy requires equal package versions and archive hashes.',
      modId: id,
      serverValue: '${server.manifest.version}@${server.packageSha256}',
      clientValue: '${client.manifest.version}@${client.packageSha256}',
    ),
  );
}

bool _mutuallyCompatible(
  String leftVersion,
  String leftRange,
  bool leftHasRange,
  String rightVersion,
  String rightRange,
  bool rightHasRange,
) =>
    _accepts(leftVersion, leftRange, leftHasRange, rightVersion) &&
    _accepts(rightVersion, rightRange, rightHasRange, leftVersion);

bool _accepts(
  String localVersion,
  String localRange,
  bool hasRange,
  String remoteVersion,
) {
  if (!hasRange) return localVersion == remoteVersion;
  try {
    return VersionRange.parse(localRange).allows(remoteVersion);
  } on FormatException {
    return false;
  }
}

bool _hasDeclaredPeerRange(ModMultiplayerProtocol protocol) =>
    protocol.peerVersionRangeIsPresent || protocol.peerVersionRange != null;

String _modeDisplay(MultiplayerAdmissionMod? item) {
  if (item == null) return 'absent';
  return item.manifest.multiplayer?.modeName ?? 'standalone-only';
}

void _addInvalid(
  String side,
  String id,
  String message,
  String value,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.invalidProfile,
      message: message,
      modId: id,
      serverValue: side == 'server' ? value : '',
      clientValue: side == 'client' ? value : '',
    ),
  );
}

int _compareMismatches(
  MultiplayerAdmissionMismatch left,
  MultiplayerAdmissionMismatch right,
) {
  var comparison = left.code.index.compareTo(right.code.index);
  if (comparison != 0) return comparison;
  comparison = left.modId.compareTo(right.modId);
  if (comparison != 0) return comparison;
  comparison = left.message.compareTo(right.message);
  if (comparison != 0) return comparison;
  comparison = left.serverValue.compareTo(right.serverValue);
  return comparison != 0
      ? comparison
      : left.clientValue.compareTo(right.clientValue);
}

String _protocolDisplay(String version, String range, bool hasRange) =>
    '$version accepts ${hasRange ? range : 'exactly $version'}';

bool _equalStrings(List<String> left, List<String> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
