part of '../models.dart';

void _validateManifestMultiplayer(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  void error(String message) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message: message,
      ),
    );
  }

  // Routed through the shared predicate rather than a literal: a gate that
  // silently stops applying when the contract moves is worse than one that
  // rejects, and this one had already stopped.
  if (!ModManifest.isSupportedSchemaVersion(manifest.schemaVersion)) return;

  final multiplayer = manifest.multiplayer;
  if (!manifest.multiplayerIsPresent || multiplayer == null) {
    return;
  }

  final mode = multiplayer.mode;
  if (mode == null) {
    error('multiplayer.mode must be client-local, server-only, or session.');
    return;
  }

  if (mode != ModMultiplayerMode.session) {
    if (multiplayer.presenceIsPresent ||
        multiplayer.protocolIsPresent ||
        multiplayer.synchronizedFilesIsPresent) {
      error(
        'multiplayer.presence, protocol, and synchronizedFiles are only valid for session mode.',
      );
    }
    return;
  }

  if (!multiplayer.presenceIsPresent || multiplayer.presence == null) {
    error('Session multiplayer requires presence to be required or optional.');
  }
  final protocol = multiplayer.protocol;
  if (!multiplayer.protocolIsPresent || protocol == null) {
    error('Session multiplayer requires a protocol object.');
  } else {
    if (SemanticVersion.tryParse(protocol.version) == null) {
      error('multiplayer.protocol.version must be an exact semantic version.');
    }
    if (protocol.peerVersionRangeIsPresent) {
      final range = protocol.peerVersionRange;
      if (range == null || range.trim().isEmpty) {
        error('multiplayer.protocol.peerVersionRange cannot be empty.');
      } else {
        try {
          VersionRange.parse(range);
        } on FormatException {
          error(
            'multiplayer.protocol.peerVersionRange must be a valid version range.',
          );
        }
      }
    }
  }

  if (multiplayer.synchronizedFiles.length > 256) {
    error(
      'multiplayer.synchronizedFiles cannot contain more than 256 entries.',
    );
  }
  final paths = <String>{};
  for (final path in multiplayer.synchronizedFiles) {
    final collisionKey = _portableManifestPathCollisionKey(path);
    if (collisionKey == null) {
      error(
        'multiplayer.synchronizedFiles entry $path must be a safe portable relative path.',
      );
    } else if (!paths.add(collisionKey)) {
      error(
        'multiplayer.synchronizedFiles contains duplicate or portable-collision path $path.',
      );
    }
    final normalizedPath = path.toLowerCase();
    if (normalizedPath == 'topiaforge.mod.json' ||
        normalizedPath == 'topiaforge.install.json') {
      error(
        'multiplayer.synchronizedFiles cannot include generated package metadata $path.',
      );
    }
  }
}
