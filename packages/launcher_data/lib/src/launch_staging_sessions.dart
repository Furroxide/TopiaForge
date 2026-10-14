part of 'launch_staging_store.dart';

/// The session channels and multiplayer preferences in manager staging. All
/// are sorted JSON written atomically; none carries an address.
///
/// - `session-handoff-<request key>.json`, game to launcher: the game asks
///   the launcher that owns it to run the join flow.
/// - `session-join-<request key>.json`, launcher to game: a live join for a
///   game whose packages already match the server's.
/// - `multiplayer-preferences-v1.json`, launcher to game: the preferences
///   projection.
/// - `multiplayer-preferences-journal-v1.json`, game to launcher: changes the
///   player made in game.
extension LaunchStagingSessions on LaunchStagingStore {
  static const preferencesFile = 'multiplayer-preferences-v1.json';
  static const journalFile = 'multiplayer-preferences-journal-v1.json';

  /// Takes the handoff the game staged for the process launched as
  /// [requestId]: returns it and removes it, once. A malformed or foreign
  /// handoff is never acted on and stays inspectable.
  Future<SessionHandoff?> takeSessionHandoff(String requestId) async {
    final file = _channel('session-handoff', requestId);
    try {
      final text = await _readText(file, LaunchTransport.maxChannelBytes);
      if (text == null) return null;
      final handoff = LaunchTransport.readSessionHandoff(text);
      if (handoff.requestId != requestId) return null;
      _directories();
      _ordinaryFile(file);
      // Remove only what was read; a newer handoff waits for the next poll.
      if (await _readText(file, LaunchTransport.maxChannelBytes) != text) {
        return null;
      }
      _directories();
      _ordinaryFile(file);
      file.deleteSync();
      return handoff;
    } on Object {
      return null;
    }
  }

  /// Stages a live join for the running process [SessionJoinRequest.requestId].
  /// A newer join replaces one the game has not taken yet.
  Future<File> writeSessionJoin(SessionJoinRequest value) => _replace(
    _channel('session-join', value.requestId),
    LaunchTransport.writeSessionJoin(value),
  );

  /// Withdraws [expected] if the game has not taken it.
  Future<void> withdrawSessionJoin(SessionJoinRequest expected) async {
    final file = _channel('session-join', expected.requestId);
    final staged = LaunchTransport.writeSessionJoin(expected);
    try {
      if (await _readText(file, LaunchTransport.maxChannelBytes) != staged) {
        return;
      }
      _directories();
      _ordinaryFile(file);
      file.deleteSync();
    } on Object {
      /* A foreign or changed join is not ours to remove. */
    }
  }

  /// Rewrites the preferences projection the game reads.
  Future<File> writePreferences(MultiplayerPreferencesProjection value) =>
      _replace(
        File(p.join(_staging, preferencesFile)),
        LaunchTransport.writePreferences(value),
      );

  /// The projection last written, or null when there is none or it cannot
  /// be read; a damaged one is then replaced by the next sync.
  Future<MultiplayerPreferencesProjection?> readPreferences() async {
    try {
      final text = await _readText(
        File(p.join(_staging, preferencesFile)),
        LaunchTransport.maxChannelBytes,
      );
      return text == null ? null : LaunchTransport.readPreferences(text);
    } on Object {
      return null;
    }
  }

  /// Takes in the game's journal and brings the projection up to date with
  /// [source], the multiplayer store and service configuration, writing it
  /// when anything changed. When the result says the game changed a
  /// preference, the caller keeps its settings and hosting defaults.
  Future<MultiplayerPreferencesSync> syncPreferences(
    MultiplayerPreferencesSource source,
  ) async {
    final sync = MultiplayerPreferencesSync.of(
      previous: await readPreferences(),
      journal: await readPreferencesJournal(),
      source: source,
    );
    if (sync.needsWrite) await writePreferences(sync.projection);
    return sync;
  }

  /// The changes the player made in game, or null when there are none or
  /// the journal cannot be read. An unreadable journal is never applied.
  Future<MultiplayerPreferencesJournal?> readPreferencesJournal() async {
    try {
      final text = await _readText(
        File(p.join(_staging, journalFile)),
        LaunchTransport.maxChannelBytes,
      );
      return text == null ? null : LaunchTransport.readPreferencesJournal(text);
    } on Object {
      return null;
    }
  }

  /// Writes [text] to [file] through a same-directory temporary file, so a
  /// reader sees the old document or the new one, never part of either.
  Future<File> _replace(File file, String text) async {
    final bytes = utf8.encode(text);
    if (bytes.length > LaunchTransport.maxChannelBytes) {
      throw const FormatException('Session channel exceeds its byte limit.');
    }
    _directories(create: true);
    _ordinaryFile(file, allowMissing: true);
    final random = Random.secure();
    final nonce = List.generate(
      16,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
    final temp = File('${file.path}.tmp-$nonce');
    var created = false;
    try {
      temp.createSync(exclusive: true);
      created = true;
      _directories();
      _ordinaryFile(temp);
      final output = await temp.open(mode: FileMode.write);
      try {
        await output.writeFrom(bytes);
        await output.flush();
      } finally {
        await output.close();
      }
      _directories();
      _ordinaryFile(temp);
      _ordinaryFile(file, allowMissing: true);
      temp.renameSync(file.path);
      created = false;
      return file;
    } finally {
      if (created) {
        try {
          _directories();
          _ordinaryFile(temp, allowMissing: true);
          if (temp.existsSync()) temp.deleteSync();
        } on Object {
          /* Never follow a substituted ancestor to remove an unowned file. */
        }
      }
    }
  }
}
