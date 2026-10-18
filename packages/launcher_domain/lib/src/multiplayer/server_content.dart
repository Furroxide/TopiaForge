part of '../multiplayer.dart';

/// How a record of server content read, or why it cannot be used.
enum ServerContentStatus {
  /// There is no record: nothing was installed for servers.
  missing,

  /// A record of [ServerContentRecord.schemaVersion], every entry as the
  /// contract says.
  valid,

  /// A newer launcher wrote it. This one must not guess at it, so it never
  /// reads or writes over it.
  newer,

  /// Anything else in the record's place: bytes that are not the contract,
  /// a link or a folder. Nothing of it is read.
  damaged,

  /// It is there but could not be read just now.
  unreadable;

  /// Whether the record says what is withheld: it is valid, or there is
  /// none. Otherwise nothing may assume a version is the player's own.
  bool get isUsable => this == valid || this == missing;
}

/// What [ServerContentRecord.read] made of a record's bytes.
final class ServerContentRecordRead {
  const ServerContentRecordRead._(this.status, this.record, this.problem);

  /// [ServerContentStatus.valid], [ServerContentStatus.newer] or
  /// [ServerContentStatus.damaged].
  final ServerContentStatus status;

  /// The record, when [status] is [ServerContentStatus.valid].
  final ServerContentRecord? record;

  /// Why the bytes are not a record, for the launcher log; empty when they
  /// are one.
  final String problem;
}

/// What joins installed into one game install, `server-content.json` beside
/// its manager state: each archive, the versions withheld after a repair,
/// and the mods whose manager record a join added because they were new to
/// the player.
///
/// The launcher writes it; the launcher and the game's loader both read it,
/// to keep a server's versions out of the player's own play. The contract is
/// held by `tests/fixtures/multiplayer/server-content`, which both languages
/// replay: exactly the members `schemaVersion` (1), `versions`, `withheld`
/// and `addedRecords`, at most [maximumBytes] bytes of UTF-8 and
/// [maximumEntries] entries in each list, no member twice anywhere, and no
/// version twice in one list. Anything else is damaged and read not at all:
/// a reader that dropped a damaged entry would expose the version it
/// withheld.
///
/// A version counts as server content only while its install receipt still
/// has the SHA-256 recorded here. A join that would pass a bound is refused,
/// never trimmed, for the same reason.
final class ServerContentRecord {
  ServerContentRecord._(
    Iterable<ServerContentEntry> entries,
    Iterable<WithheldServerVersion> withheld,
    Iterable<String> addedRecords,
  ) : entries = List.unmodifiable(entries),
      withheld = List.unmodifiable(withheld),
      addedRecords = Set.unmodifiable(addedRecords);

  ServerContentRecord.empty()
    : entries = const [],
      withheld = const [],
      addedRecords = const {};

  static const schemaVersion = 1;
  static const maximumEntries = 1024;
  static const maximumBytes = 1024 * 1024;

  /// Each installed archive once, by mod id and version.
  final List<ServerContentEntry> entries;

  /// Each version withheld after a repair once, by mod id and version.
  final List<WithheldServerVersion> withheld;

  /// The mods, by lowercase id, whose manager record a join added.
  final Set<String> addedRecords;

  bool get isEmpty =>
      entries.isEmpty && withheld.isEmpty && addedRecords.isEmpty;

  /// The entry for [modId] at [version], if any.
  ServerContentEntry? entryFor(String modId, String version) {
    for (final entry in entries) {
      if (entry.names(modId, version)) return entry;
    }
    return null;
  }

  /// Whether the player's own play passes over [modId] (in any case) at
  /// [version] while its install receipt has the SHA-256 [receiptSha256]:
  /// a hidden entry or a withheld version with that receipt. Null stands for
  /// a receipt that cannot be read, which withholds every version the record
  /// names that way, failing closed. As the game's loader decides.
  bool withholds(String modId, String version, String? receiptSha256) {
    bool matches(String recorded) =>
        receiptSha256 == null || recorded == receiptSha256;
    for (final entry in entries) {
      if (entry.hidden &&
          entry.names(modId, version) &&
          matches(entry.receiptSha256)) {
        return true;
      }
    }
    for (final item in withheld) {
      if (item.names(modId, version) && matches(item.receiptSha256)) {
        return true;
      }
    }
    return false;
  }

  /// This record with [installed] added, each replacing any entry or
  /// withheld version for the same mod and version, and [addedRecords]
  /// noted. Throws [StateError] when the result would pass [maximumEntries].
  ServerContentRecord withInstalled(
    Iterable<ServerContentEntry> installed, {
    Iterable<String> addedRecords = const [],
  }) {
    final added = installed.toList();
    bool replaced(String id, String version) =>
        added.any((item) => item.names(id, version));
    final kept = [
      for (final entry in entries)
        if (!replaced(entry.modId, entry.version)) entry,
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
    return ServerContentRecord._(
      [...kept, ...added],
      [
        for (final item in withheld)
          if (!replaced(item.modId, item.version)) item,
      ],
      ids,
    );
  }

  /// This record with [versions] withheld too, each version once. Throws
  /// [StateError] when the result would pass [maximumEntries].
  ServerContentRecord withWithheld(Iterable<WithheldServerVersion> versions) {
    final all = [...withheld];
    for (final item in versions) {
      if (!all.any((kept) => kept.names(item.modId, item.version))) {
        all.add(item);
      }
    }
    if (all.length > maximumEntries) {
      throw StateError('Too many installed versions to withhold.');
    }
    return ServerContentRecord._(entries, all, addedRecords);
  }

  /// This record without the entries [removed] and the versions [unwithheld]
  /// name, and without the added records of [addedRecords].
  ServerContentRecord without(
    Iterable<ServerContentEntry> removed, {
    Iterable<String> addedRecords = const [],
    Iterable<WithheldServerVersion> unwithheld = const [],
  }) {
    final gone = removed.toList();
    final released = unwithheld.toList();
    final ids = {for (final id in addedRecords) id.toLowerCase()};
    return ServerContentRecord._(
      [
        for (final entry in entries)
          if (!gone.any((item) => item.names(entry.modId, entry.version)))
            entry,
      ],
      [
        for (final item in withheld)
          if (!released.any((other) => other.names(item.modId, item.version)))
            item,
      ],
      this.addedRecords.where((id) => !ids.contains(id)),
    );
  }

  /// The canonical bytes of this record: members in ordinal order, no white
  /// space, each list sorted by lowercase id, then version.
  Uint8List encode() => CanonicalJson.encode({
    'schemaVersion': schemaVersion,
    'versions': [
      for (final entry
          in [...entries]..sort(
            (left, right) => _compareVersionKeys(
              left.modId,
              left.version,
              right.modId,
              right.version,
            ),
          ))
        entry.toJson(),
    ],
    'withheld': [
      for (final item
          in [...withheld]..sort(
            (left, right) => _compareVersionKeys(
              left.modId,
              left.version,
              right.modId,
              right.version,
            ),
          ))
        item.toJson(),
    ],
    'addedRecords': [...addedRecords]..sort(),
  });

  /// Reads a record's bytes as the contract says, or says why they are not
  /// one. Never throws.
  static ServerContentRecordRead read(List<int> bytes) {
    ServerContentRecordRead damaged(String problem) =>
        ServerContentRecordRead._(ServerContentStatus.damaged, null, problem);
    if (bytes.length > maximumBytes) {
      return damaged('The record is larger than $maximumBytes bytes.');
    }
    // Dart's UTF-8 decoder drops a leading byte order mark without a word,
    // and the game's reader refuses one, so it is looked for in the bytes.
    if (bytes.length >= 3 &&
        bytes[0] == 0xEF &&
        bytes[1] == 0xBB &&
        bytes[2] == 0xBF) {
      return damaged('The record starts with a byte order mark.');
    }
    final Object? document;
    try {
      final text = utf8.decode(bytes);
      rejectDuplicateJsonProperties(text, label: 'The record');
      document = jsonDecode(text);
    } on FormatException catch (error) {
      return damaged(error.message);
    }
    if (document is! Map) return damaged('The record is not a JSON object.');
    final schema = document['schemaVersion'];
    if (schema is! int) {
      return damaged('The record has no integer schemaVersion.');
    }
    if (schema > schemaVersion) {
      return ServerContentRecordRead._(
        ServerContentStatus.newer,
        null,
        'A newer launcher wrote this record (schemaVersion $schema).',
      );
    }
    if (schema != schemaVersion ||
        !_hasMembers(document, const {
          'schemaVersion',
          'versions',
          'withheld',
          'addedRecords',
        })) {
      return damaged('The record does not have exactly the contract members.');
    }
    final entries = _readList(
      document['versions'],
      ServerContentEntry._tryRead,
    );
    final withheld = _readList(
      document['withheld'],
      WithheldServerVersion._tryRead,
    );
    final added = _readList(document['addedRecords'], _readAddedRecord);
    if (entries == null || withheld == null || added == null) {
      return damaged('An entry of the record is not as the contract says.');
    }
    if (!_unique(entries.map((item) => _key(item.modId, item.version))) ||
        !_unique(withheld.map((item) => _key(item.modId, item.version))) ||
        !_unique(added)) {
      return damaged('The record names one version twice in a list.');
    }
    return ServerContentRecordRead._(
      ServerContentStatus.valid,
      ServerContentRecord._(entries, withheld, added),
      '',
    );
  }

  /// What damaged bytes still hold, for a launcher repairing the record:
  /// each entry, withheld version and added record that reads on its own,
  /// the first of any that repeat, within the bounds. Empty when nothing
  /// reads. Never throws.
  static ServerContentRecord salvage(List<int> bytes) {
    try {
      if (bytes.length > maximumBytes) return ServerContentRecord.empty();
      final document = jsonDecode(utf8.decode(bytes));
      if (document is! Map) return ServerContentRecord.empty();
      List<T> keep<T>(
        Object? list,
        T? Function(Object?) read,
        String Function(T) key,
      ) {
        final kept = <T>[];
        final seen = <String>{};
        if (list is! List) return kept;
        for (final item in list) {
          final value = read(item);
          if (value != null &&
              kept.length < maximumEntries &&
              seen.add(key(value))) {
            kept.add(value);
          }
        }
        return kept;
      }

      return ServerContentRecord._(
        keep(
          document['versions'],
          ServerContentEntry._tryRead,
          (item) => _key(item.modId, item.version),
        ),
        keep(
          document['withheld'],
          WithheldServerVersion._tryRead,
          (item) => _key(item.modId, item.version),
        ),
        keep(document['addedRecords'], _readAddedRecord, (id) => id),
      );
    } on Object {
      return ServerContentRecord.empty();
    }
  }

  static List<T>? _readList<T>(Object? value, T? Function(Object?) read) {
    if (value is! List || value.length > maximumEntries) return null;
    final items = <T>[];
    for (final item in value) {
      final parsed = read(item);
      if (parsed == null) return null;
      items.add(parsed);
    }
    return items;
  }

  static String? _readAddedRecord(Object? value) =>
      value is String &&
          DescriptorPackage.isModId(value) &&
          value == value.toLowerCase()
      ? value
      : null;

  static bool _unique(Iterable<String> keys) {
    final seen = <String>{};
    return keys.every(seen.add);
  }
}
