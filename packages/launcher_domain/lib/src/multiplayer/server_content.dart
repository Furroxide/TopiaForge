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
    if (!DescriptorPackage.isModId(modId)) {
      throw ArgumentError('Not a mod id.', 'modId');
    }
    if (!_isSemanticVersion(version)) {
      throw ArgumentError('Not a semantic version.', 'version');
    }
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

  /// Whether the player's own profiles never see this version: the mod was
  /// already the player's, installed, recorded or named by one of their
  /// profiles, when the join installed it. A mod new to the player is
  /// visible, but its manager record starts disabled.
  final bool hidden;

  /// Unix seconds.
  final int installedAt;

  /// Whether this entry is for [id] (in any case) at [version].
  bool names(String id, String version) =>
      modId.toLowerCase() == id.toLowerCase() && this.version == version;

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
        value.length != 6 ||
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
}

/// A record read from disk, and whether anything in it had to be dropped or
/// rewritten.
typedef ServerContentRecordRead = ({ServerContentRecord record, bool damaged});

/// What joins installed into one game install: each archive, and the mods
/// whose manager record a join added because they were new to the player.
///
/// The record is how the launcher tells server content from the player's
/// own: a version counts as server content only while its install receipt
/// still has the SHA-256 recorded here. It is bounded at [maximumEntries]
/// versions and as many added records; a join that would pass either bound
/// is refused, never trimmed, because a dropped entry would turn a server's
/// version into one the player's own profiles could pick.
final class ServerContentRecord {
  ServerContentRecord._(
    List<ServerContentEntry> entries,
    Iterable<String> addedRecords,
  ) : entries = List.unmodifiable(entries),
      addedRecords = Set.unmodifiable(addedRecords);

  ServerContentRecord.empty() : entries = const [], addedRecords = const {};

  static const schema = 'topiaforge.server-content/1';
  static const maximumEntries = 1024;
  static const maximumBytes = 512 * 1024;

  /// Each installed archive once, by mod id and version.
  final List<ServerContentEntry> entries;

  /// The mods, by lowercase id, whose manager record a join added.
  final Set<String> addedRecords;

  bool get isEmpty => entries.isEmpty && addedRecords.isEmpty;

  /// The entry for [modId] at [version], if any.
  ServerContentEntry? entryFor(String modId, String version) {
    for (final entry in entries) {
      if (entry.names(modId, version)) return entry;
    }
    return null;
  }

  /// This record with [installed] added, each replacing any entry for the
  /// same mod and version, and [addedRecords] noted. Throws [StateError]
  /// when the result would pass [maximumEntries].
  ServerContentRecord withInstalled(
    Iterable<ServerContentEntry> installed, {
    Iterable<String> addedRecords = const [],
  }) {
    final added = installed.toList();
    final kept = [
      for (final entry in entries)
        if (!added.any((item) => item.names(entry.modId, entry.version))) entry,
    ];
    final ids = {
      ...this.addedRecords,
      for (final id in addedRecords) id.toLowerCase(),
    };
    if (kept.length + added.length > maximumEntries ||
        ids.length > maximumEntries) {
      throw StateError(
        'The record of content installed for servers is full. Remove some '
        'server content, then join again.',
      );
    }
    return ServerContentRecord._([...kept, ...added], ids);
  }

  /// This record without the entries [removed] names and without the added
  /// records of [addedRecords].
  ServerContentRecord without(
    Iterable<ServerContentEntry> removed, {
    Iterable<String> addedRecords = const [],
  }) {
    final gone = removed.toList();
    final ids = {for (final id in addedRecords) id.toLowerCase()};
    return ServerContentRecord._([
      for (final entry in entries)
        if (!gone.any((item) => item.names(entry.modId, entry.version))) entry,
    ], this.addedRecords.where((id) => !ids.contains(id)));
  }

  /// The canonical bytes of this record.
  Uint8List encode() => CanonicalJson.encode({
    'schema': schema,
    'versions': [for (final entry in entries) entry.toJson()],
    'addedRecords': [...addedRecords]..sort(),
  });

  /// Reads a stored record, keeping every entry that is valid. Entries that
  /// do not read, repeat a version or pass the bound are dropped, and so is
  /// anything else the schema does not have; [ServerContentRecordRead.damaged]
  /// says whether any was, or the bytes were not in canonical form. Null when
  /// nothing can be used: the bytes are not a JSON object, or the record was
  /// written for another schema, which this launcher must not guess at.
  static ServerContentRecordRead? read(List<int> bytes) {
    if (bytes.length > maximumBytes) return null;
    final Object? document;
    try {
      document = jsonDecode(utf8.decode(bytes));
    } on FormatException {
      return null;
    }
    if (document is! Map || document['schema'] != schema) return null;
    var damaged = document.keys.any(
      (key) => key != 'schema' && key != 'versions' && key != 'addedRecords',
    );
    final entries = <ServerContentEntry>[];
    final versions = document['versions'];
    if (versions is List) {
      for (final item in versions) {
        final entry = ServerContentEntry._tryRead(item);
        if (entry == null ||
            entries.length == maximumEntries ||
            entries.any((kept) => kept.names(entry.modId, entry.version))) {
          damaged = true;
        } else {
          entries.add(entry);
        }
      }
    } else if (document.containsKey('versions')) {
      damaged = true;
    }
    final ids = <String>{};
    final added = document['addedRecords'];
    if (added is List) {
      for (final id in added) {
        final lower = id is String && DescriptorPackage.isModId(id)
            ? id.toLowerCase()
            : null;
        if (lower != null &&
            !ids.contains(lower) &&
            ids.length < maximumEntries) {
          ids.add(lower);
        } else {
          damaged = true;
        }
      }
    } else if (document.containsKey('addedRecords')) {
      damaged = true;
    }
    final record = ServerContentRecord._(entries, ids);
    final encoded = record.encode();
    if (encoded.length != bytes.length) {
      damaged = true;
    } else {
      for (var index = 0; index < encoded.length; index++) {
        if (encoded[index] != bytes[index]) {
          damaged = true;
          break;
        }
      }
    }
    return (record: record, damaged: damaged);
  }
}
