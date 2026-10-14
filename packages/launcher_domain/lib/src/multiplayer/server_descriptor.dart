part of '../multiplayer.dart';

/// How a hosted session can be found.
enum ServerVisibility {
  /// Listed in the Internet browser and announced on the LAN.
  public('public'),

  /// Reachable only through an invite code or link.
  invite('invite'),

  /// Announced on the local network only.
  lan('lan');

  const ServerVisibility(this.wireName);

  final String wireName;

  static ServerVisibility? tryParse(String value) {
    for (final visibility in values) {
      if (visibility.wireName == value) return visibility;
    }
    return null;
  }
}

/// The public description of a hosted session (`topiaforge.server-descriptor/1`)
/// that a joining player checks before connecting: who the host is, the
/// transport key the handshake must present, and the exact packages to
/// install.
///
/// The host signs it with its Ed25519 key ([SignedServerDescriptorV1]), and the
/// server id is the SHA-256 of that key, so a link, listing or LAN beacon
/// that names a server id pins everything here. It never holds a network
/// address.
final class ServerDescriptorV1 {
  ServerDescriptorV1({
    required List<int> signingKey,
    required List<int> transportKey,
    required this.issuedAt,
    required this.name,
    required this.gameBuild,
    required this.runtimeVersion,
    required this.protocolVersion,
    required this.maxPlayers,
    required this.passwordRequired,
    required this.voice,
    required this.visibility,
    required this.hostServesPackages,
    required this.gamemode,
    required this.world,
    required List<DescriptorPackage> packages,
  }) : signingKey = _key(signingKey, 'signingKey'),
       transportKey = _key(transportKey, 'transportKey'),
       packages = List.unmodifiable(packages),
       serverId = serverIdOf(signingKey),
       packageDigest = packageDigestOf(_sortedPackages(packages)) {
    if (issuedAt < 0 || issuedAt > CanonicalJson.maximumSafeInteger) {
      throw RangeError.range(
        issuedAt,
        0,
        CanonicalJson.maximumSafeInteger,
        'issuedAt',
      );
    }
    if (!WireText.isDisplayText(name, maximumNameLength)) {
      throw ArgumentError.value(
        name,
        'name',
        'A server name is 1 to 48 printable characters.',
      );
    }
    if (!isGameBuild(gameBuild)) {
      throw ArgumentError.value(
        gameBuild,
        'gameBuild',
        'A game build is 1 to 16 letters, digits, dots, dashes or underscores.',
      );
    }
    if (!isSemanticVersion(runtimeVersion)) {
      throw ArgumentError.value(
        runtimeVersion,
        'runtimeVersion',
        'The runtime version is a semantic version.',
      );
    }
    if (!isSemanticVersion(protocolVersion)) {
      throw ArgumentError.value(
        protocolVersion,
        'protocolVersion',
        'The protocol version is a semantic version.',
      );
    }
    if (maxPlayers < minimumPlayers || maxPlayers > maximumPlayers) {
      throw RangeError.range(
        maxPlayers,
        minimumPlayers,
        maximumPlayers,
        'maxPlayers',
      );
    }
    for (final (field, value) in [('gamemode', gamemode), ('world', world)]) {
      if (value != null &&
          !WireText.isDisplayText(value, maximumTargetNameLength)) {
        throw ArgumentError.value(
          value,
          field,
          'A $field name is 1 to 48 printable characters.',
        );
      }
    }
  }

  static const schema = 'topiaforge.server-descriptor/1';
  static const keyBytes = 32;
  static const maximumNameLength = 48;
  static const maximumTargetNameLength = 48;
  static const maximumPackages = 512;
  static const minimumPlayers = 2;
  static const maximumPlayers = 16;

  /// The host's Ed25519 public key.
  final Uint8List signingKey;

  /// The host's X25519 static key, which the transport handshake must present.
  final Uint8List transportKey;

  /// The lowercase hexadecimal SHA-256 of [signingKey].
  final String serverId;

  /// The signing time in Unix seconds.
  final int issuedAt;

  final String name;
  final String gameBuild;
  final String runtimeVersion;
  final String protocolVersion;
  final int maxPlayers;
  final bool passwordRequired;
  final bool voice;
  final ServerVisibility visibility;

  /// Whether the host, in developer mode, serves its own package archives to
  /// joining players.
  final bool hostServesPackages;

  final String? gamemode;
  final String? world;

  /// The host's packages, sorted by id, each listed once.
  final List<DescriptorPackage> packages;

  /// The lowercase hexadecimal SHA-256 of the canonical package list, which a
  /// join plan pins.
  final String packageDigest;

  /// The descriptor object in the version 1 member set.
  Map<String, Object?> toJson() => {
    'schema': schema,
    'serverId': serverId,
    'signingKey': WireText.toBase64Url(signingKey),
    'transportKey': WireText.toBase64Url(transportKey),
    'issuedAt': issuedAt,
    'name': name,
    'gameBuild': gameBuild,
    'runtimeVersion': runtimeVersion,
    'protocolVersion': protocolVersion,
    'maxPlayers': maxPlayers,
    'passwordRequired': passwordRequired,
    'voice': voice,
    'visibility': visibility.wireName,
    'hostServesPackages': hostServesPackages,
    'gamemode': gamemode,
    'world': world,
    'packages': [for (final package in packages) package.toJson()],
    'packageDigest': packageDigest,
  };

  /// The canonical bytes the host signs.
  Uint8List get canonicalBytes => CanonicalJson.encode(toJson());

  static const _members = {
    'schema',
    'serverId',
    'signingKey',
    'transportKey',
    'issuedAt',
    'name',
    'gameBuild',
    'runtimeVersion',
    'protocolVersion',
    'maxPlayers',
    'passwordRequired',
    'voice',
    'visibility',
    'hostServesPackages',
    'gamemode',
    'world',
    'packages',
    'packageDigest',
  };

  /// Reads a descriptor object, refusing any unknown, missing or inconsistent
  /// member. Throws [FormatException].
  factory ServerDescriptorV1.fromJson(Object? value) {
    if (value is! Map<String, Object?> || !_hasExactly(value, _members)) {
      throw const FormatException(
        'A server descriptor has exactly the version 1 members.',
      );
    }
    if (_requireString(value, 'schema') != schema) {
      throw const FormatException('The descriptor schema is not $schema.');
    }
    final signingKey = WireText.tryParseBase64Url(
      _requireString(value, 'signingKey'),
      keyBytes,
    );
    if (signingKey == null) {
      throw const FormatException('The signing key is malformed.');
    }
    final transportKey = WireText.tryParseBase64Url(
      _requireString(value, 'transportKey'),
      keyBytes,
    );
    if (transportKey == null) {
      throw const FormatException('The transport key is malformed.');
    }
    final visibility = ServerVisibility.tryParse(
      _requireString(value, 'visibility'),
    );
    if (visibility == null) {
      throw FormatException("Unknown visibility '${value['visibility']}'.");
    }
    final packages = _readPackages(value['packages']);
    final ServerDescriptorV1 descriptor;
    try {
      descriptor = ServerDescriptorV1(
        signingKey: signingKey,
        transportKey: transportKey,
        issuedAt: _requireInteger(value, 'issuedAt'),
        name: _requireString(value, 'name'),
        gameBuild: _requireString(value, 'gameBuild'),
        runtimeVersion: _requireString(value, 'runtimeVersion'),
        protocolVersion: _requireString(value, 'protocolVersion'),
        maxPlayers: _requireInteger(value, 'maxPlayers'),
        passwordRequired: _requireBoolean(value, 'passwordRequired'),
        voice: _requireBoolean(value, 'voice'),
        visibility: visibility,
        hostServesPackages: _requireBoolean(value, 'hostServesPackages'),
        gamemode: _optionalString(value, 'gamemode'),
        world: _optionalString(value, 'world'),
        packages: packages,
      );
    } on ArgumentError catch (error) {
      throw FormatException('The descriptor is invalid: ${error.message}');
    }
    if (_requireString(value, 'serverId') != descriptor.serverId) {
      throw const FormatException(
        'The server id does not match the signing key.',
      );
    }
    if (_requireString(value, 'packageDigest') != descriptor.packageDigest) {
      throw const FormatException(
        'The package digest does not match the packages.',
      );
    }
    return descriptor;
  }

  /// The server id of [signingKey]: its SHA-256 in lowercase hexadecimal.
  static String serverIdOf(List<int> signingKey) =>
      PureSha256.hex(_key(signingKey, 'signingKey'));

  /// The SHA-256 of the canonical package list, in lowercase hexadecimal.
  static String packageDigestOf(List<DescriptorPackage> packages) =>
      PureSha256.hex(
        CanonicalJson.encode([
          for (final package in packages) package.toJson(),
        ]),
      );

  /// Whether [value] is a semantic version of at most 64 characters, as the
  /// descriptor and session info formats require.
  static bool isSemanticVersion(String? value) => _isSemanticVersion(value);

  /// Whether [value] is a Robotopia build as the multiplayer formats spell
  /// it: 1 to 16 of `[0-9A-Za-z._-]`.
  static bool isGameBuild(String? value) => _isGameBuild(value);

  static Uint8List _key(List<int> key, String name) {
    if (key.length != keyBytes) {
      throw ArgumentError.value(key.length, name, 'Keys are 32 bytes.');
    }
    final copy = Uint8List(keyBytes);
    for (var index = 0; index < keyBytes; index++) {
      final value = key[index];
      if (value < 0 || value > 0xFF) {
        throw ArgumentError.value(value, name, 'Each value must be a byte.');
      }
      copy[index] = value;
    }
    return copy.asUnmodifiableView();
  }

  static List<DescriptorPackage> _sortedPackages(
    List<DescriptorPackage> packages,
  ) {
    if (packages.length > maximumPackages) {
      throw ArgumentError.value(
        packages.length,
        'packages',
        'A descriptor lists at most $maximumPackages packages.',
      );
    }
    for (var index = 1; index < packages.length; index++) {
      if (packages[index - 1].id.compareTo(packages[index].id) >= 0) {
        throw ArgumentError.value(
          packages[index].id,
          'packages',
          'Packages are listed once each, in ordinal order of their ids.',
        );
      }
    }
    return packages;
  }

  static List<DescriptorPackage> _readPackages(Object? value) {
    if (value is! List<Object?>) {
      throw const FormatException('The packages member is a list.');
    }
    if (value.length > maximumPackages) {
      throw const FormatException(
        'A descriptor lists at most $maximumPackages packages.',
      );
    }
    return [for (final item in value) DescriptorPackage.fromJson(item)];
  }
}
