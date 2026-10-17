/// The launcher's own multiplayer files: settings with favorites and
/// history, and the trust-on-first-use record for typed addresses.
library;

import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'multiplayer_store_salvage.dart';

/// What loading did about a stored file.
enum MultiplayerStoreRecovery {
  /// It read cleanly, or did not exist yet.
  none,

  /// Part of it was damaged: the original was set aside and what was valid
  /// is kept.
  salvaged,

  /// Nothing in it could be used: the original was set aside and defaults
  /// replace it.
  reset,

  /// It could not be read just now, as when another program holds it. The
  /// file is left as it is, nothing is written over it until it reads again,
  /// and [MultiplayerStore.unreadableSettings] stand in meanwhile. Nothing on
  /// this computer, such as link registration, should change to match them.
  unreadable,
}

final class MultiplayerSettingsLoad {
  const MultiplayerSettingsLoad(this.settings, this.recovery);

  final MultiplayerSettings settings;
  final MultiplayerStoreRecovery recovery;
}

/// A change that was not made: the file could not be read, so it was left
/// as it is ([unreadable]), or the result could not be written. [message]
/// names neither the file nor anything in it.
final class MultiplayerStoreException implements Exception {
  const MultiplayerStoreException(this.message, {this.unreadable = false});

  final String message;

  /// Whether the stored file could not be read just now, so no check could
  /// use it and nothing was written over it.
  final bool unreadable;

  @override
  String toString() => 'MultiplayerStoreException: $message';
}

/// Keeps the launcher's multiplayer state in one folder:
///
/// - `settings.json`: [MultiplayerSettings] with its schema version, the
///   favorites and the history. Servers are named by server id and their
///   last-known name only. The domain never stores an address there: a
///   hand-typed address is typed again.
/// - `known-hosts.json`: the domain's `topiaforge.known-hosts/1` record, which
///   holds hashes of typed addresses, never the addresses.
///
/// Every write replaces its file atomically, and the writes of one store
/// run one after another, so a slow save never lands after a newer one. A
/// damaged file is never fatal: it is set aside as
/// `<name>.damaged-<UTC time>.json`, the newest [keptDamagedCopies] kept, and
/// what was valid in it, or the defaults, take its place.
///
/// A file that cannot be read just now, as when another program holds it,
/// is never replaced. Saves and updates throw [MultiplayerStoreException]
/// until it reads again, the settings load as [unreadableSettings], and
/// loading the known hosts throws, so no check mistakes an unread record for
/// an empty one.
///
/// Where Windows refuses to replace a held file, a write moves the old one
/// aside as `<name>.<token>.bak` first. A write stopped between its two moves
/// leaves that backup and no file, so reading restores the newest backup
/// when the file is missing, as the launcher's own files do. While that
/// backup cannot be moved, the file counts as one that cannot be read.
final class MultiplayerStore {
  MultiplayerStore(this.directory, {DateTime Function()? clock})
    : _clock = clock ?? DateTime.now;

  static const settingsFileName = 'settings.json';
  static const knownHostsFileName = 'known-hosts.json';

  /// Settings larger than this are damaged; a full store is a few kilobytes.
  static const maximumSettingsBytes = 256 * 1024;
  static const keptDamagedCopies = 3;

  /// What stands in for settings that cannot be read. Everything that
  /// reaches the network or changes this computer is off, and nothing is
  /// acknowledged or consented to: LAN discovery, UPnP, voice and link
  /// handling are off, privacy is relay-only, and a direct path still needs
  /// the disclosure. They are never saved.
  static final unreadableSettings = MultiplayerSettings(
    lanDiscoveryEnabled: false,
    linkHandlingEnabled: false,
  );

  final String directory;
  final DateTime Function() _clock;
  final Random _random = Random();
  Future<void> _tail = Future.value();

  File get _settingsFile => File(p.join(directory, settingsFileName));
  File get _knownHostsFile => File(p.join(directory, knownHostsFileName));

  /// Reads the settings, recovering from any damage. Never throws: while the
  /// file cannot be read, gives [unreadableSettings] and
  /// [MultiplayerStoreRecovery.unreadable].
  Future<MultiplayerSettingsLoad> loadSettings() => _serial(_loadSettings);

  /// Writes [settings]. Throws [MultiplayerStoreException] when they cannot
  /// be written, and while the stored settings cannot be read, which are
  /// then left as they are.
  Future<MultiplayerSettings> saveSettings(MultiplayerSettings settings) =>
      _serial(() async {
        await _readableSettings();
        await _writeSettings(settings);
        return settings;
      });

  /// Reads the settings, applies [change] and writes the result, with no
  /// other save in between. Throws [MultiplayerStoreException] as
  /// [saveSettings] does, before [change] runs when they cannot be read.
  Future<MultiplayerSettings> updateSettings(
    MultiplayerSettings Function(MultiplayerSettings current) change,
  ) => _serial(() async {
    final next = change(await _readableSettings());
    await _writeSettings(next);
    return next;
  });

  /// Reads the known-hosts record, recovering from any damage. Throws
  /// [MultiplayerStoreException] while it cannot be read, so no check can
  /// mistake an unread record for an empty one.
  Future<KnownHosts> loadKnownHosts() => _serial(_loadKnownHosts);

  /// Reads the known-hosts record, applies [change] and writes the result
  /// when it differs. Throws [MultiplayerStoreException] while the record
  /// cannot be read, before [change] runs, and when the result cannot be
  /// written.
  Future<KnownHosts> updateKnownHosts(
    KnownHosts Function(KnownHosts current) change,
  ) => _serial(() async {
    final current = await _loadKnownHosts();
    final next = change(current);
    final encoded = next.encode();
    if (!_sameBytes(encoded, current.encode())) {
      await _write(_knownHostsFile, encoded, 'known hosts');
    }
    return next;
  });

  Future<T> _serial<T>(Future<T> Function() action) {
    final result = _tail.then((_) => action());
    _tail = result.then((_) {}, onError: (Object _) {});
    return result;
  }

  Future<MultiplayerSettingsLoad> _loadSettings() async {
    final file = _settingsFile;
    final read = await _readBounded(file, maximumSettingsBytes);
    switch (read) {
      case _Missing():
        return MultiplayerSettingsLoad(
          MultiplayerSettings(),
          MultiplayerStoreRecovery.none,
        );
      case _Unreadable():
        return MultiplayerSettingsLoad(
          unreadableSettings,
          MultiplayerStoreRecovery.unreadable,
        );
      case _Damaged():
        return _replaceDamaged(file, null);
      case _Read(:final bytes):
        final SalvagedMultiplayerSettings? salvaged;
        try {
          salvaged = salvageMultiplayerSettings(jsonDecode(utf8.decode(bytes)));
        } on FormatException {
          return _replaceDamaged(file, null);
        }
        if (salvaged == null) return _replaceDamaged(file, null);
        if (salvaged.damaged) return _replaceDamaged(file, salvaged.settings);
        return MultiplayerSettingsLoad(
          salvaged.settings,
          MultiplayerStoreRecovery.none,
        );
    }
  }

  /// Sets a damaged settings file aside and writes [kept], or the defaults,
  /// in its place. Failing to do either still yields usable settings.
  Future<MultiplayerSettingsLoad> _replaceDamaged(
    File file,
    MultiplayerSettings? kept,
  ) async {
    final settings = kept ?? MultiplayerSettings();
    final recovery = kept == null
        ? MultiplayerStoreRecovery.reset
        : MultiplayerStoreRecovery.salvaged;
    if (await _setAside(file)) {
      try {
        await _writeSettings(settings);
      } on MultiplayerStoreException {
        // The next save tries again; these settings are in use meanwhile.
      }
    }
    return MultiplayerSettingsLoad(settings, recovery);
  }

  /// The stored settings, recovered from any damage; throws while they
  /// cannot be read, so nothing is written over them.
  Future<MultiplayerSettings> _readableSettings() async {
    final load = await _loadSettings();
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      throw _unreadable('multiplayer settings');
    }
    return load.settings;
  }

  static MultiplayerStoreException _unreadable(String label) =>
      MultiplayerStoreException(
        'The $label could not be read just now, so nothing was changed. Try '
        'again in a moment.',
        unreadable: true,
      );

  Future<KnownHosts> _loadKnownHosts() async {
    final file = _knownHostsFile;
    final read = await _readBounded(file, KnownHosts.maximumBytes);
    switch (read) {
      case _Missing():
        return KnownHosts.empty();
      case _Unreadable():
        throw _unreadable('known hosts');
      case _Damaged():
        await _setAside(file);
        return KnownHosts.empty();
      case _Read(:final bytes):
        // The domain keeps what is valid; anything it had to drop means the
        // file was damaged, so the original is kept aside for inspection.
        final hosts = KnownHosts.decode(bytes);
        final encoded = hosts.encode();
        if (!_sameBytes(encoded, bytes) && await _setAside(file)) {
          try {
            await _write(file, encoded, 'known hosts');
          } on MultiplayerStoreException {
            // The next change writes it again.
          }
        }
        return hosts;
    }
  }

  Future<void> _writeSettings(MultiplayerSettings settings) => _write(
    _settingsFile,
    utf8.encode(
      '${const JsonEncoder.withIndent('  ').convert(settings.toJson())}\n',
    ),
    'multiplayer settings',
  );

  Future<_ReadResult> _readBounded(File file, int maximumBytes) async {
    FileSystemEntityType type;
    try {
      type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type == FileSystemEntityType.notFound) {
        switch (await _restoreBackup(file)) {
          case _Restore.held:
            // What the backup holds is unread, not missing.
            return const _Unreadable();
          case _Restore.restored:
            type = await FileSystemEntity.type(file.path, followLinks: false);
          case _Restore.none:
            break;
        }
      }
    } on FileSystemException {
      return const _Unreadable();
    }
    if (type == FileSystemEntityType.notFound) return const _Missing();
    // A link or a folder in a file's place is set aside, never followed.
    if (type != FileSystemEntityType.file) return const _Damaged();
    try {
      if (await file.length() > maximumBytes) return const _Damaged();
      final bytes = await file.readAsBytes();
      return bytes.length > maximumBytes ? const _Damaged() : _Read(bytes);
    } on FileSystemException {
      return const _Unreadable();
    }
  }

  /// Puts back the newest `<name>.<token>.bak` of [file], which a write
  /// stopped between its two moves left behind, and drops older ones. Only
  /// regular files count, never a link or a folder. A backup that is still
  /// there but cannot be moved just now, as when another program holds it,
  /// is [_Restore.held]: an older one never stands in for it, and nothing is
  /// dropped.
  Future<_Restore> _restoreBackup(File file) async {
    final prefix = '${p.basename(file.path)}.';
    final backups = <(File, DateTime)>[];
    try {
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final name = p.basename(entity.path);
        if (entity is File &&
            name.startsWith(prefix) &&
            name.endsWith('.bak')) {
          backups.add((entity, (await entity.stat()).modified));
        }
      }
    } on FileSystemException {
      return _Restore.none;
    }
    backups.sort((left, right) => right.$2.compareTo(left.$2));
    for (final (backup, _) in backups) {
      try {
        await backup.rename(file.path);
      } on FileSystemException {
        // One that vanished meanwhile is passed over; one still there is
        // held, and only it has the latest contents.
        if (await _exists(backup)) return _Restore.held;
        continue;
      }
      for (final (stale, _) in backups) {
        if (!identical(stale, backup)) await _deleteEntity(stale.path);
      }
      return _Restore.restored;
    }
    return _Restore.none;
  }

  /// Renames [file] to a damaged copy beside it and keeps the newest
  /// [keptDamagedCopies]; false when it could not be moved.
  Future<bool> _setAside(File file) async {
    final stem = p.basenameWithoutExtension(file.path);
    final stamp = _clock().toUtc().toIso8601String().replaceAll(
      RegExp(r'[^0-9TZ]'),
      '',
    );
    var target = File(p.join(directory, '$stem.damaged-$stamp.json'));
    for (var attempt = 2; await _exists(target) && attempt < 100; attempt++) {
      target = File(p.join(directory, '$stem.damaged-$stamp-$attempt.json'));
    }
    try {
      final type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        await Link(file.path).rename(target.path);
      } else if (type == FileSystemEntityType.directory) {
        await Directory(file.path).rename(target.path);
      } else {
        await file.rename(target.path);
      }
    } on FileSystemException {
      return false;
    }
    await _pruneDamaged(stem);
    return true;
  }

  Future<void> _pruneDamaged(String stem) async {
    try {
      final copies = <String>[];
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final name = p.basename(entity.path);
        if (name.startsWith('$stem.damaged-') && name.endsWith('.json')) {
          copies.add(entity.path);
        }
      }
      copies.sort();
      for (final path in copies.take(
        max(0, copies.length - keptDamagedCopies),
      )) {
        await _deleteEntity(path);
      }
    } on FileSystemException {
      // Old copies are only tidied; leaving one costs nothing.
    }
  }

  /// Writes [bytes] to a temporary file beside [file] and moves it into
  /// place, so a reader sees the old file or the new one, never a mix.
  Future<void> _write(File file, List<int> bytes, String label) async {
    final token =
        '$pid-${_clock().microsecondsSinceEpoch}-${_random.nextInt(1 << 30)}';
    final temporary = File('${file.path}.$token.tmp');
    try {
      await Directory(directory).create(recursive: true);
      final type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type != FileSystemEntityType.notFound &&
          type != FileSystemEntityType.file &&
          !await _setAside(file)) {
        throw const FileSystemException('Not a regular file.');
      }
      await temporary.writeAsBytes(bytes, flush: true);
      await _moveIntoPlace(temporary, file, token);
    } on FileSystemException {
      throw MultiplayerStoreException('The $label could not be saved.');
    } finally {
      await _deleteEntity(temporary.path);
    }
  }

  Future<void> _moveIntoPlace(File temporary, File file, String token) async {
    try {
      await temporary.rename(file.path);
      return;
    } on FileSystemException {
      if (!await _exists(file)) rethrow;
    }
    // Windows can refuse to replace a file another program holds for a
    // moment: move it out of the way, put the new one in, then drop it.
    final backup = File('${file.path}.$token.bak');
    await file.rename(backup.path);
    try {
      await temporary.rename(file.path);
    } on FileSystemException {
      if (!await _exists(file)) await backup.rename(file.path);
      rethrow;
    }
    await _deleteEntity(backup.path);
  }

  static Future<bool> _exists(File file) async =>
      await FileSystemEntity.type(file.path, followLinks: false) !=
      FileSystemEntityType.notFound;

  /// Deletes a file or a link itself. A folder is left alone: the store
  /// never deletes anything it did not write.
  static Future<void> _deleteEntity(String path) async {
    try {
      final type = await FileSystemEntity.type(path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        await Link(path).delete();
      } else if (type == FileSystemEntityType.file) {
        await File(path).delete();
      }
    } on FileSystemException {
      // Best effort: a leftover temporary file or old copy is harmless.
    }
  }

  static bool _sameBytes(List<int> left, List<int> right) {
    if (left.length != right.length) return false;
    for (var index = 0; index < left.length; index++) {
      if (left[index] != right[index]) return false;
    }
    return true;
  }
}

/// What [MultiplayerStore._restoreBackup] did.
enum _Restore {
  /// There was no backup to put back.
  none,

  /// The newest backup is back in the file's place.
  restored,

  /// The newest backup could not be moved just now.
  held,
}

sealed class _ReadResult {
  const _ReadResult();
}

final class _Missing extends _ReadResult {
  const _Missing();
}

final class _Unreadable extends _ReadResult {
  const _Unreadable();
}

final class _Damaged extends _ReadResult {
  const _Damaged();
}

final class _Read extends _ReadResult {
  const _Read(this.bytes);

  final List<int> bytes;
}
