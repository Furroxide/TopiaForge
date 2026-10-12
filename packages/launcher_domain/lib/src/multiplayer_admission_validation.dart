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
  if (!ModManifest.isSupportedSchemaVersion(manifest.schemaVersion)) {
    errors.add('schemaVersion must be 6');
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
      'multiplayer.mode must be client-local, server-only, session, or standalone',
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
