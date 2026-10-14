part of '../multiplayer.dart';

/// What a host tells a browser through the transport's info query before
/// any handshake (`topiaforge.session-info/1`): canonical JSON of at most
/// 1,188 bytes, the info query's limit.
///
/// It is public and unauthenticated, so it names the server and its signed
/// descriptor by hash and never carries a network address. A browser trusts
/// nothing in it until the descriptor it names has been fetched and verified.
final class SessionInfo {
  SessionInfo({
    required this.serverId,
    required this.descriptorSha256,
    required this.name,
    required this.gameBuild,
    required this.runtimeVersion,
    required this.protocolVersion,
    required this.gamemode,
    required this.world,
    required this.players,
    required this.maxPlayers,
    required this.passwordRequired,
    required this.voice,
  }) {
    for (final (field, value) in [
      ('serverId', serverId),
      ('descriptorSha256', descriptorSha256),
    ]) {
      if (!WireText.isHex(value, 32)) {
        throw ArgumentError(
          'Must be 64 lowercase hexadecimal characters.',
          field,
        );
      }
    }
    if (!WireText.isDisplayText(name, ServerDescriptorV1.maximumNameLength)) {
      throw ArgumentError(
        'A server name is 1 to 48 printable characters.',
        'name',
      );
    }
    if (!ServerDescriptorV1.isGameBuild(gameBuild)) {
      throw ArgumentError('Not a game build.', 'gameBuild');
    }
    for (final (field, value) in [
      ('runtimeVersion', runtimeVersion),
      ('protocolVersion', protocolVersion),
    ]) {
      if (!ServerDescriptorV1.isSemanticVersion(value)) {
        throw ArgumentError('Not a semantic version.', field);
      }
    }
    for (final (field, value) in [('gamemode', gamemode), ('world', world)]) {
      if (value != null &&
          !WireText.isDisplayText(
            value,
            ServerDescriptorV1.maximumTargetNameLength,
          )) {
        throw ArgumentError(
          'A $field name is 1 to 48 printable characters.',
          field,
        );
      }
    }
    if (maxPlayers < ServerDescriptorV1.minimumPlayers ||
        maxPlayers > ServerDescriptorV1.maximumPlayers) {
      throw RangeError.range(
        maxPlayers,
        ServerDescriptorV1.minimumPlayers,
        ServerDescriptorV1.maximumPlayers,
        'maxPlayers',
      );
    }
    if (players < 0 || players > maxPlayers) {
      throw RangeError.range(players, 0, maxPlayers, 'players');
    }
  }

  static const schema = 'topiaforge.session-info/1';
  static const maximumBytes = 1188;

  final String serverId;

  /// The SHA-256 of the signed descriptor the host serves.
  final String descriptorSha256;
  final String name;
  final String gameBuild;
  final String runtimeVersion;
  final String protocolVersion;
  final String? gamemode;
  final String? world;
  final int players;
  final int maxPlayers;
  final bool passwordRequired;
  final bool voice;

  static const _members = {
    'schema',
    'serverId',
    'descriptorSha256',
    'name',
    'gameBuild',
    'runtimeVersion',
    'protocolVersion',
    'gamemode',
    'world',
    'players',
    'maxPlayers',
    'passwordRequired',
    'voice',
  };

  Map<String, Object?> toJson() => {
    'schema': schema,
    'serverId': serverId,
    'descriptorSha256': descriptorSha256,
    'name': name,
    'gameBuild': gameBuild,
    'runtimeVersion': runtimeVersion,
    'protocolVersion': protocolVersion,
    'gamemode': gamemode,
    'world': world,
    'players': players,
    'maxPlayers': maxPlayers,
    'passwordRequired': passwordRequired,
    'voice': voice,
  };

  /// The canonical bytes a host sends. Every field is bounded, so no valid
  /// info reaches the limit; the check guards the arithmetic.
  Uint8List encode() {
    final bytes = CanonicalJson.encode(toJson());
    if (bytes.length > maximumBytes) {
      throw StateError('Session info outgrew the info query limit.');
    }
    return bytes;
  }

  /// Reads info bytes, refusing anything not canonical, unknown or missing
  /// members, and invalid values. Throws [FormatException].
  factory SessionInfo.decode(List<int> bytes) {
    final document = CanonicalJson.decode(bytes, maximumBytes: maximumBytes);
    if (document is! Map<String, Object?> || !_hasExactly(document, _members)) {
      throw const FormatException(
        'Session info has exactly the version 1 members.',
      );
    }
    if (_requireString(document, 'schema') != schema) {
      throw const FormatException('The session info schema is not $schema.');
    }
    try {
      return SessionInfo(
        serverId: _requireString(document, 'serverId'),
        descriptorSha256: _requireString(document, 'descriptorSha256'),
        name: _requireString(document, 'name'),
        gameBuild: _requireString(document, 'gameBuild'),
        runtimeVersion: _requireString(document, 'runtimeVersion'),
        protocolVersion: _requireString(document, 'protocolVersion'),
        gamemode: _optionalString(document, 'gamemode'),
        world: _optionalString(document, 'world'),
        players: _requireInteger(document, 'players'),
        maxPlayers: _requireInteger(document, 'maxPlayers'),
        passwordRequired: _requireBoolean(document, 'passwordRequired'),
        voice: _requireBoolean(document, 'voice'),
      );
    } on ArgumentError catch (error) {
      throw FormatException('The session info is invalid: ${error.message}');
    }
  }

  /// [SessionInfo.decode], or null when the bytes are refused.
  static SessionInfo? tryDecode(List<int> bytes) {
    try {
      return SessionInfo.decode(bytes);
    } on FormatException {
      return null;
    }
  }
}
