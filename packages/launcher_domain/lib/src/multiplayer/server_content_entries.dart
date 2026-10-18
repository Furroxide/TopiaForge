part of '../multiplayer.dart';

/// One archive a join installed into a game install.
final class ServerContentEntry {
  ServerContentEntry({
    required this.modId,
    required this.version,
    required this.sha256,
    required this.receiptSha256,
    required this.hidden,
    required this.installedAt,
  }) {
    _requireVersionKey(modId, version);
    if (!WireText.isHex(sha256, 32)) {
      throw ArgumentError('Not a SHA-256.', 'sha256');
    }
    if (!WireText.isHex(receiptSha256, 32)) {
      throw ArgumentError('Not a SHA-256.', 'receiptSha256');
    }
    if (installedAt < 0 || installedAt > CanonicalJson.maximumSafeInteger) {
      throw RangeError.range(
        installedAt,
        0,
        CanonicalJson.maximumSafeInteger,
        'installedAt',
      );
    }
  }

  /// The mod id as its manifest spells it.
  final String modId;
  final String version;

  /// The archive's SHA-256.
  final String sha256;

  /// The SHA-256 of the install receipt the join wrote. A version whose
  /// receipt no longer matches was installed again since, by the player or a
  /// repair, and is no longer server content.
  final String receiptSha256;

  /// Whether the player's own play never sees this version: the mod was
  /// already the player's, installed, recorded or named by one of their
  /// profiles, when the join installed it. A mod new to the player is
  /// visible, but its manager record starts disabled.
  final bool hidden;

  /// Unix seconds.
  final int installedAt;

  /// Whether this entry is for [id] (in any case) at [version].
  bool names(String id, String version) =>
      _sameVersion(modId, this.version, id, version);

  Map<String, Object?> toJson() => {
    'id': modId,
    'version': version,
    'sha256': sha256,
    'receipt': receiptSha256,
    'hidden': hidden,
    'installedAt': installedAt,
  };

  static ServerContentEntry? _tryRead(Object? value) {
    if (value is! Map ||
        !_hasMembers(value, _entryMembers) ||
        value['id'] is! String ||
        value['version'] is! String ||
        value['sha256'] is! String ||
        value['receipt'] is! String ||
        value['hidden'] is! bool ||
        value['installedAt'] is! int) {
      return null;
    }
    try {
      return ServerContentEntry(
        modId: value['id'] as String,
        version: value['version'] as String,
        sha256: value['sha256'] as String,
        receiptSha256: value['receipt'] as String,
        hidden: value['hidden'] as bool,
        installedAt: value['installedAt'] as int,
      );
    } on ArgumentError {
      return null;
    }
  }

  static const _entryMembers = {
    'id',
    'version',
    'sha256',
    'receipt',
    'hidden',
    'installedAt',
  };
}

/// An installed version the record keeps out of the player's own play
/// without knowing it as server content. A launcher that finds the record
/// damaged cannot tell which installed versions joins brought in, so it
/// withholds each one the manager state does not name, by its install
/// receipt, rather than risk exposing a server's version. Unpinned selection
/// never takes it; an exact pin still does, as the game's loader loads every
/// exact pin, so a profile that pins it keeps loading it. Installing it
/// again, which writes another receipt, makes it the player's.
final class WithheldServerVersion {
  WithheldServerVersion({
    required this.modId,
    required this.version,
    required this.receiptSha256,
  }) {
    _requireVersionKey(modId, version);
    if (!WireText.isHex(receiptSha256, 32)) {
      throw ArgumentError('Not a SHA-256.', 'receiptSha256');
    }
  }

  final String modId;
  final String version;
  final String receiptSha256;

  /// Whether this is [id] (in any case) at [version].
  bool names(String id, String version) =>
      _sameVersion(modId, this.version, id, version);

  Map<String, Object?> toJson() => {
    'id': modId,
    'version': version,
    'receipt': receiptSha256,
  };

  static WithheldServerVersion? _tryRead(Object? value) {
    if (value is! Map ||
        !_hasMembers(value, const {'id', 'version', 'receipt'}) ||
        value['id'] is! String ||
        value['version'] is! String ||
        value['receipt'] is! String) {
      return null;
    }
    try {
      return WithheldServerVersion(
        modId: value['id'] as String,
        version: value['version'] as String,
        receiptSha256: value['receipt'] as String,
      );
    } on ArgumentError {
      return null;
    }
  }
}

/// `<lowercase id>@<version>`: one installed version, whatever the id's case.
String _key(String id, String version) => '${id.toLowerCase()}@$version';

bool _sameVersion(String id, String version, String otherId, String other) =>
    version == other && id.toLowerCase() == otherId.toLowerCase();

/// The record's order: by lowercase id, then version, as the fixtures'
/// generator sorts them. Ids and versions are ASCII, so code units and code
/// points order them alike.
int _compareVersionKeys(
  String leftId,
  String leftVersion,
  String rightId,
  String rightVersion,
) {
  final byId = leftId.toLowerCase().compareTo(rightId.toLowerCase());
  return byId != 0 ? byId : leftVersion.compareTo(rightVersion);
}

bool _hasMembers(Map<Object?, Object?> value, Set<String> names) =>
    value.length == names.length && names.every(value.containsKey);

void _requireVersionKey(String modId, String version) {
  if (!DescriptorPackage.isModId(modId)) {
    throw ArgumentError('Not a mod id.', 'modId');
  }
  if (!_isSemanticVersion(version)) {
    throw ArgumentError('Not a semantic version.', 'version');
  }
}
