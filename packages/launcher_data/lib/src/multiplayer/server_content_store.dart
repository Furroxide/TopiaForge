part of 'multiplayer_store.dart';

/// What loading the record of server content found.
final class ServerContentLoad {
  const ServerContentLoad(
    this.record,
    this.status, {
    this.problem = '',
    this.damagedBytes,
  });

  /// The record when [status] is [ServerContentStatus.valid], and empty
  /// when it is [ServerContentStatus.missing]. Otherwise empty too, and it
  /// must not be taken to mean that nothing is withheld.
  final ServerContentRecord record;
  final ServerContentStatus status;

  /// Why the record cannot be used, for the launcher log; empty when it can.
  final String problem;

  /// What a damaged record's file held, for its repair; null for anything
  /// else, and for a link, a folder, a file past the bound, or a record a
  /// repair already moved aside.
  final List<int>? damagedBytes;
}

/// The record of what joins installed into one game install
/// ([ServerContentRecord]), kept beside that install's manager state as
/// `server-content.json`.
///
/// It lives with the install rather than in the launcher's data folder, so
/// it moves with the packages it describes when the game is moved, and a
/// launch or install that holds the install's staging lease is the only
/// writer.
///
/// The game's loader reads it too, so the store never guesses: a record is
/// read whole or not at all ([ServerContentRecord.read]). Writes replace it
/// atomically and run one after another. A record that is newer, or cannot
/// be read just now, is never written over; one that is damaged is written
/// over only by [repair], with a copy of it kept aside as
/// `server-content.damaged-<UTC time>.json`.
///
/// The desktop launcher and the command line can both have the install
/// open. [update] and [repair] run only under the install's staging lease,
/// which keeps their writes apart, and only they put back what a write
/// stopped between its two moves left aside. A [load] reads that backup
/// where it stands, as the game's loader does, and never moves anything: a
/// backup moved back by a reader could land on the record a write in the
/// other process had just moved in, and replace it with the older one.
final class ServerContentStore {
  ServerContentStore(this.directory, {DateTime Function()? clock})
    : _files = _StoreFiles(directory, clock ?? DateTime.now);

  static const fileName = 'server-content.json';

  final String directory;
  final _StoreFiles _files;
  Future<void> _tail = Future.value();
  var _swept = false;

  File get _file => File(p.join(directory, fileName));

  /// Reads the record. Never throws, and never moves or writes anything.
  Future<ServerContentLoad> load() => _serial(() => _load(restore: false));

  /// Reads the record, applies [change] and writes the result when it
  /// differs, with no other write in between. Throws
  /// [MultiplayerStoreException] before [change] runs while the record
  /// cannot be used, and when the result cannot be written. Whatever
  /// [change] throws is passed on, and nothing is written. Runs under the
  /// install's staging lease.
  Future<ServerContentRecord> update(
    ServerContentRecord Function(ServerContentRecord current) change,
  ) => _serial(() async {
    final load = await _load(restore: true);
    if (!load.status.isUsable) throw _unusable(load.status);
    final next = change(load.record);
    final encoded = next.encode();
    // A missing record reads as the empty one, so an empty result leaves it
    // missing.
    if (!_sameBytes(encoded, load.record.encode())) {
      await _files.write(_file, encoded, 'record of server content');
    }
    return next;
  });

  /// Replaces a damaged record with what [rebuild] makes of what is left of
  /// it ([ServerContentRecord.salvage]), keeping a copy of the damaged one
  /// aside. Anything other than a damaged record is left as it is. Returns
  /// the record as it stands afterwards: still damaged when [rebuild] or the
  /// write failed, which the launcher log says. Runs under the install's
  /// staging lease.
  ///
  /// A link, a folder or a file past the bound is moved aside before the
  /// record that replaces it is written, so a write that fails or stops
  /// there leaves the damaged copy with no record beside it, which still
  /// reads as damaged, never as a record that withholds nothing.
  Future<ServerContentLoad> repair(
    Future<ServerContentRecord> Function(ServerContentRecord salvaged) rebuild,
  ) => _serial(() async {
    final load = await _load(restore: true);
    if (load.status != ServerContentStatus.damaged) return load;
    final bytes = load.damagedBytes;
    final ServerContentRecord rebuilt;
    try {
      rebuilt = await rebuild(
        bytes == null
            ? ServerContentRecord.empty()
            : ServerContentRecord.salvage(bytes),
      );
    } on Object catch (error) {
      return ServerContentLoad(
        ServerContentRecord.empty(),
        ServerContentStatus.damaged,
        problem: '${load.problem} It could not be repaired: $error',
        damagedBytes: bytes,
      );
    }
    final kept = bytes != null
        ? await _files.keepCopy(_file, bytes)
        // With no record there, an earlier repair already moved it aside.
        : !await _StoreFiles._exists(_file) || await _files.setAside(_file);
    if (!kept) {
      return ServerContentLoad(
        ServerContentRecord.empty(),
        ServerContentStatus.damaged,
        problem: '${load.problem} No copy of it could be kept aside.',
        damagedBytes: bytes,
      );
    }
    try {
      await _files.write(_file, rebuilt.encode(), 'record of server content');
    } on MultiplayerStoreException {
      return ServerContentLoad(
        ServerContentRecord.empty(),
        ServerContentStatus.damaged,
        problem: '${load.problem} The repaired record could not be saved.',
        damagedBytes: bytes,
      );
    }
    return ServerContentLoad(rebuilt, ServerContentStatus.valid);
  });

  Future<T> _serial<T>(Future<T> Function() action) {
    final result = _tail.then((_) async {
      if (!_swept) {
        _swept = true;
        await _files.sweepTemporaries(const [
          fileName,
        ], MultiplayerStore.staleTemporaryAge);
      }
      return action();
    });
    _tail = result.then((_) {}, onError: (Object _) {});
    return result;
  }

  Future<ServerContentLoad> _load({required bool restore}) async {
    final read = await _files.read(
      _file,
      ServerContentRecord.maximumBytes,
      restore: restore,
    );
    switch (read) {
      case _Missing():
        try {
          if (await _files.hasDamagedCopy(
            p.basenameWithoutExtension(fileName),
          )) {
            return ServerContentLoad(
              ServerContentRecord.empty(),
              ServerContentStatus.damaged,
              problem:
                  'The record of server content was set aside as damaged, '
                  'and the record that repairs it was not written.',
            );
          }
        } on FileSystemException {
          return ServerContentLoad(
            ServerContentRecord.empty(),
            ServerContentStatus.unreadable,
            problem: 'The record of server content could not be read just now.',
          );
        }
        return ServerContentLoad(
          ServerContentRecord.empty(),
          ServerContentStatus.missing,
        );
      case _Unreadable():
        return ServerContentLoad(
          ServerContentRecord.empty(),
          ServerContentStatus.unreadable,
          problem: 'The record of server content could not be read just now.',
        );
      case _Damaged():
        return ServerContentLoad(
          ServerContentRecord.empty(),
          ServerContentStatus.damaged,
          problem:
              'A link, a folder or a file past '
              '${ServerContentRecord.maximumBytes} bytes stands where the '
              'record of server content belongs.',
        );
      case _Read(:final bytes):
        final read = ServerContentRecord.read(bytes);
        return ServerContentLoad(
          read.record ?? ServerContentRecord.empty(),
          read.status,
          problem: read.problem,
          damagedBytes: read.status == ServerContentStatus.damaged
              ? bytes
              : null,
        );
    }
  }

  static MultiplayerStoreException _unusable(ServerContentStatus status) =>
      switch (status) {
        ServerContentStatus.newer => const MultiplayerStoreException(
          'A newer TopiaForge launcher recorded the mods it installed for '
          'servers in this game, so this launcher changes nothing there. '
          'Update the launcher, then try again.',
        ),
        ServerContentStatus.damaged => const MultiplayerStoreException(
          'The record of mods installed for servers is damaged, so nothing '
          'was changed. Restart the launcher to repair it, then try again.',
        ),
        _ => MultiplayerStore._unreadable('record of server content'),
      };
}
