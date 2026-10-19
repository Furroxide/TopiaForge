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
/// - `launcher-quit-<request key>.json`, launcher to game: the launcher asks a
///   game it started to quit before a restart.
///
/// A taker claims a channel file by renaming it to a private name before it
/// reads it, so a newer file the other side writes meanwhile lands at the
/// channel's name again and is never removed unread. The claim's name holds
/// its claim time, so a claim a crashed take left behind can be swept.
extension LaunchStagingSessions on LaunchStagingStore {
  static const preferencesFile = 'multiplayer-preferences-v1.json';
  static const journalFile = 'multiplayer-preferences-journal-v1.json';

  /// How old a claim must be before a sweep removes it. A live take holds
  /// its claim for milliseconds.
  static const staleClaimAge = Duration(hours: 1);

  /// Takes the handoff the game staged for the process launched as
  /// [requestId]: returns it and removes it, once. A malformed or foreign
  /// handoff is removed without being acted on. Claims a crashed take left
  /// behind are swept first ([sweepStaleClaims]).
  Future<SessionHandoff?> takeSessionHandoff(String requestId) async {
    final file = _channel('session-handoff', requestId);
    final File claimed;
    try {
      if (!_directories()) return null;
      if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
          FileSystemEntityType.notFound) {
        return null;
      }
      _ordinaryFile(file);
      final now = DateTime.now();
      sweepStaleClaims(now: now);
      claimed = File(
        '${file.path}.taking-${now.millisecondsSinceEpoch}-${_nonce()}',
      );
      file.renameSync(claimed.path);
    } on Object {
      return null;
    }
    try {
      _afterClaim?.call();
      final text = await _readText(claimed, LaunchTransport.maxChannelBytes);
      if (text == null) return null;
      final handoff = LaunchTransport.readSessionHandoff(text);
      return handoff.requestId == requestId ? handoff : null;
    } on Object {
      return null;
    } finally {
      _removeClaim(claimed);
    }
  }

  /// Stages a live join for the running process [SessionJoinRequest.requestId].
  /// A newer join replaces one the game has not taken yet. The caller
  /// serializes the writes and withdrawals of one request.
  Future<File> writeSessionJoin(SessionJoinRequest value) => _replace(
    _channel('session-join', value.requestId),
    LaunchTransport.writeSessionJoin(value),
  );

  /// Withdraws [expected] if the game has not taken it, and says whether it
  /// did. False means the game took it, or it was already gone or replaced.
  Future<bool> withdrawSessionJoin(SessionJoinRequest expected) => _withdraw(
    _channel('session-join', expected.requestId),
    LaunchTransport.writeSessionJoin(expected),
  );

  /// Asks the process launched as [LauncherQuitRequest.requestId] to quit
  /// (`launcher-quit-<request key>.json`). Its manager looks twice a second,
  /// takes the request once, and quits as the game's Quit button would.
  Future<File> writeLauncherQuit(LauncherQuitRequest value) => _replace(
    _channel('launcher-quit', value.requestId),
    LaunchTransport.writeLauncherQuit(value),
  );

  /// Whether [expected] still waits at its name: false once the game took it,
  /// or it was withdrawn or replaced, or when it cannot be read.
  Future<bool> isLauncherQuitPending(LauncherQuitRequest expected) async {
    try {
      return await _readText(
            _channel('launcher-quit', expected.requestId),
            LaunchTransport.maxChannelBytes,
          ) ==
          LaunchTransport.writeLauncherQuit(expected);
    } on Object {
      return false;
    }
  }

  /// Withdraws [expected] if the game has not taken it, and says whether it
  /// did. False means the game took it, or it was already gone or replaced.
  Future<bool> withdrawLauncherQuit(LauncherQuitRequest expected) => _withdraw(
    _channel('launcher-quit', expected.requestId),
    LaunchTransport.writeLauncherQuit(expected),
  );

  Future<bool> _withdraw(File file, String staged) async {
    try {
      if (await _readText(file, LaunchTransport.maxChannelBytes) != staged) {
        return false;
      }
      _directories();
      _ordinaryFile(file);
      file.deleteSync();
      return true;
    } on Object {
      /* A foreign or changed file is not ours to remove. */
      return false;
    }
  }

  /// Rewrites the preferences projection the game reads.
  Future<File> writePreferences(MultiplayerPreferencesProjection value) =>
      _replace(
        File(p.join(_staging, preferencesFile)),
        LaunchTransport.writePreferences(value),
      );

  /// The projection last written, or null when there is none or it cannot
  /// be read; a damaged one is then rewritten from the launcher's store.
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

  /// One preferences round ([MultiplayerPreferencesSync]): takes in the
  /// game's journal and brings the projection up to date with [source], the
  /// launcher's store and service configuration. When the projection must
  /// change, [save] keeps the result in the store first, and only then is
  /// the projection written, so a lost projection never re-applies an old
  /// change or lowers the revision. If [save] throws, nothing is written.
  Future<MultiplayerPreferencesSync> syncPreferences(
    MultiplayerPreferencesSource source, {
    required Future<void> Function(MultiplayerPreferencesSync sync) save,
  }) async {
    final sync = MultiplayerPreferencesSync.of(
      previous: await readPreferences(),
      journal: await readPreferencesJournal(),
      source: source,
    );
    if (!sync.needsWrite) return sync;
    await save(sync);
    await writePreferences(sync.projection);
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
  /// reader sees the old document or the new one, never part of either. The
  /// rename is retried briefly: Windows refuses to rename over a file while
  /// another process reads it, however that reader shares it.
  Future<File> _replace(File file, String text) async {
    final bytes = utf8.encode(text);
    if (bytes.length > LaunchTransport.maxChannelBytes) {
      throw const FormatException('Session channel exceeds its byte limit.');
    }
    _directories(create: true);
    _ordinaryFile(file, allowMissing: true);
    final temp = File('${file.path}.tmp-${_nonce()}');
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
      for (var attempt = 0; ; attempt++) {
        _directories();
        _ordinaryFile(temp);
        _ordinaryFile(file, allowMissing: true);
        try {
          temp.renameSync(file.path);
          break;
        } on FileSystemException {
          if (attempt >= LaunchStagingStore.replaceRetries) rethrow;
        }
        await (_beforeRetry ?? _pause)(attempt);
      }
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

  /// Removes a claimed channel file, whatever it held.
  void _removeClaim(File claimed) {
    try {
      _directories();
      _ordinaryFile(claimed, allowMissing: true);
      if (claimed.existsSync()) claimed.deleteSync();
    } on Object {
      /* Never follow a substituted ancestor to remove an unowned file. */
    }
  }

  /// Removes the launcher's handoff claims that a crashed take left behind,
  /// and returns how many. Only its own claim pattern
  /// (`session-handoff-<key>.json.taking-<milliseconds>-<nonce>`) is
  /// touched. A rename keeps a file's timestamps, so a claim is dated by the
  /// claim time in its name, and only one more than [staleClaimAge] old is
  /// stale. A claim another process holds open is left, since Windows refuses
  /// to delete it; elsewhere only its age protects it. It never throws.
  int sweepStaleClaims({DateTime? now}) {
    final cutoff =
        (now ?? DateTime.now()).millisecondsSinceEpoch -
        staleClaimAge.inMilliseconds;
    final List<FileSystemEntity> entries;
    try {
      if (!_directories()) return 0;
      entries = Directory(_staging).listSync(followLinks: false);
    } on Object {
      return 0;
    }
    var removed = 0;
    for (final entry in entries) {
      final match = _handoffClaim.firstMatch(p.basename(entry.path));
      if (match == null || int.parse(match[1]!) >= cutoff) continue;
      try {
        final claim = File(entry.path);
        _directories();
        _ordinaryFile(claim);
        claim.deleteSync();
        removed++;
      } on Object {
        /* In use, linked or already gone: a later sweep looks again. */
      }
    }
    return removed;
  }

  static Future<void> _pause(int attempt) =>
      Future<void>.delayed(Duration(milliseconds: 10 << attempt));

  static String _nonce() {
    final random = Random.secure();
    return List.generate(
      16,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
  }
}

/// The launcher's own claims: a handoff claimed under its claim time (Unix
/// milliseconds) and a random nonce.
final _handoffClaim = RegExp(
  r'^session-handoff-[0-9a-f]{64}\.json\.taking-([0-9]{13})-[0-9a-f]{32}$',
);
