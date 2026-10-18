part of 'multiplayer_store.dart';

/// What loading the record of server content found.
final class ServerContentLoad {
  const ServerContentLoad(this.record, this.recovery);

  final ServerContentRecord record;
  final MultiplayerStoreRecovery recovery;
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
/// The file follows [MultiplayerStore]'s rules: writes replace it atomically
/// and run one after another; a damaged record is never fatal, a copy is kept
/// aside as `server-content.damaged-<UTC time>.json` and what was valid takes
/// its place; and a record that cannot be read just now is never written
/// over. Then [load] reports [MultiplayerStoreRecovery.unreadable] with an
/// empty record that must not be mistaken for an empty install, and
/// [update] throws [MultiplayerStoreException].
final class ServerContentStore {
  ServerContentStore(this.directory, {DateTime Function()? clock})
    : _files = _StoreFiles(directory, clock ?? DateTime.now);

  static const fileName = 'server-content.json';

  final String directory;
  final _StoreFiles _files;
  Future<void> _tail = Future.value();
  var _swept = false;

  File get _file => File(p.join(directory, fileName));

  /// Reads the record, recovering from any damage. Never throws.
  Future<ServerContentLoad> load() => _serial(_load);

  /// Reads the record, applies [change] and writes the result when it
  /// differs, with no other write in between. Throws
  /// [MultiplayerStoreException] while the record cannot be read, before
  /// [change] runs, and when the result cannot be written. Whatever [change]
  /// throws is passed on, and nothing is written.
  Future<ServerContentRecord> update(
    ServerContentRecord Function(ServerContentRecord current) change,
  ) => _serial(() async {
    final load = await _load();
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      throw MultiplayerStore._unreadable('record of server content');
    }
    final next = change(load.record);
    final encoded = next.encode();
    if (!_sameBytes(encoded, load.record.encode()) ||
        load.recovery != MultiplayerStoreRecovery.none) {
      await _files.write(_file, encoded, 'record of server content');
    }
    return next;
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

  Future<ServerContentLoad> _load() async {
    final read = await _files.read(_file, ServerContentRecord.maximumBytes);
    switch (read) {
      case _Missing():
        return ServerContentLoad(
          ServerContentRecord.empty(),
          MultiplayerStoreRecovery.none,
        );
      case _Unreadable():
        return ServerContentLoad(
          ServerContentRecord.empty(),
          MultiplayerStoreRecovery.unreadable,
        );
      case _Damaged():
        // A link, a folder or an oversized file in the record's place.
        await _files.setAside(_file);
        return ServerContentLoad(
          ServerContentRecord.empty(),
          MultiplayerStoreRecovery.reset,
        );
      case _Read(:final bytes):
        final salvaged = ServerContentRecord.read(bytes);
        if (salvaged != null && !salvaged.damaged) {
          return ServerContentLoad(
            salvaged.record,
            MultiplayerStoreRecovery.none,
          );
        }
        final record = salvaged?.record ?? ServerContentRecord.empty();
        // A copy of the original is kept aside, and the file stays in place
        // until what replaces it is written, so a write that fails leaves it
        // to be salvaged again.
        if (await _files.keepCopy(_file, bytes)) {
          try {
            await _files.write(
              _file,
              record.encode(),
              'record of server content',
            );
          } on MultiplayerStoreException {
            // The next read salvages it again.
          }
        }
        return ServerContentLoad(
          record,
          salvaged == null
              ? MultiplayerStoreRecovery.reset
              : MultiplayerStoreRecovery.salvaged,
        );
    }
  }
}
