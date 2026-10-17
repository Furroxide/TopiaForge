/// The launcher's own multiplayer files: settings with favorites and
/// history, and the trust-on-first-use record for typed addresses.
library;

import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'multiplayer_store_salvage.dart';

part 'multiplayer_store_files.dart';

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
/// backup cannot be moved, or the folder cannot be searched for one, the file
/// counts as one that cannot be read.
///
/// A write stopped part way can also leave its `<name>.<token>.tmp`. Each
/// store sweeps those once, before its first read or write, but only once
/// they are [staleTemporaryAge] old and never while a write in this isolate
/// uses one, so a write still running elsewhere keeps its own. The token is
/// `<process id>-<microseconds>-<random>`, and only names the store gives its
/// own files are ever put back or swept: a copy the player keeps beside a
/// file under another name is never touched.
///
/// One launcher process uses the folder at a time, since the launcher runs as
/// a single instance. Two processes sharing it could race while a backup is
/// put back.
final class MultiplayerStore {
  MultiplayerStore(this.directory, {DateTime Function()? clock})
    : _files = _StoreFiles(directory, clock ?? DateTime.now);

  static const settingsFileName = 'settings.json';
  static const knownHostsFileName = 'known-hosts.json';

  /// Settings larger than this are damaged; a full store is a few kilobytes.
  static const maximumSettingsBytes = 256 * 1024;
  static const keptDamagedCopies = 3;

  /// How old a temporary file of this store must be before a sweep deletes
  /// it. A write still running made its own moments ago.
  static const staleTemporaryAge = Duration(hours: 1);

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
  final _StoreFiles _files;
  Future<void> _tail = Future.value();
  var _swept = false;

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
      await _files.write(_knownHostsFile, encoded, 'known hosts');
    }
    return next;
  });

  /// Runs [action] after every earlier one, the first of them after the
  /// sweep of stale temporary files.
  Future<T> _serial<T>(Future<T> Function() action) {
    final result = _tail.then((_) async {
      if (!_swept) {
        _swept = true;
        await _files.sweepTemporaries(const [
          settingsFileName,
          knownHostsFileName,
        ], staleTemporaryAge);
      }
      return action();
    });
    _tail = result.then((_) {}, onError: (Object _) {});
    return result;
  }

  Future<MultiplayerSettingsLoad> _loadSettings() async {
    final file = _settingsFile;
    final read = await _files.read(file, maximumSettingsBytes);
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
    if (await _files.setAside(file)) {
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
    final read = await _files.read(file, KnownHosts.maximumBytes);
    switch (read) {
      case _Missing():
        return KnownHosts.empty();
      case _Unreadable():
        throw _unreadable('known hosts');
      case _Damaged():
        await _files.setAside(file);
        return KnownHosts.empty();
      case _Read(:final bytes):
        // The domain keeps what is valid; anything it had to drop means the
        // file was damaged, so the original is kept aside for inspection.
        final hosts = KnownHosts.decode(bytes);
        final encoded = hosts.encode();
        if (!_sameBytes(encoded, bytes) && await _files.setAside(file)) {
          try {
            await _files.write(file, encoded, 'known hosts');
          } on MultiplayerStoreException {
            // The next change writes it again.
          }
        }
        return hosts;
    }
  }

  Future<void> _writeSettings(MultiplayerSettings settings) => _files.write(
    _settingsFile,
    utf8.encode(
      '${const JsonEncoder.withIndent('  ').convert(settings.toJson())}\n',
    ),
    'multiplayer settings',
  );
}
