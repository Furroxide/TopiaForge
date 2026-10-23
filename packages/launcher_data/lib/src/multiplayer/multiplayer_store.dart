/// The launcher's own multiplayer files: settings with favorites and
/// history, the trust-on-first-use record for typed addresses, and the
/// record of what joins installed into each game install.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'multiplayer_store_salvage.dart';

export 'multiplayer_store_salvage.dart' show MultiplayerSettingsExtras;

part 'multiplayer_store_files.dart';
part 'server_content_store.dart';

/// What loading did about a stored file.
enum MultiplayerStoreRecovery {
  /// It read cleanly, or did not exist yet.
  none,

  /// Part of it was damaged: a copy of the original was kept aside and what
  /// was valid is kept.
  salvaged,

  /// Nothing in it could be used: the original, or a copy of it, was kept
  /// aside and defaults replace it.
  reset,

  /// It could not be read just now, as when another program holds it. The
  /// file is left as it is, nothing is written over it until it reads again,
  /// and [MultiplayerStore.unreadableSettings] stand in meanwhile. Nothing on
  /// this computer, such as link registration, should change to match them.
  unreadable,
}

final class MultiplayerSettingsLoad {
  const MultiplayerSettingsLoad(
    this.settings,
    this.recovery, {
    this.extras = MultiplayerSettingsExtras.none,
  });

  final MultiplayerSettings settings;
  final MultiplayerStoreRecovery recovery;

  /// What the file keeps beside the settings; none while it cannot be read.
  final MultiplayerSettingsExtras extras;
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
///   favorites and the history, and beside them the [MultiplayerSettingsExtras]:
///   the hosting defaults, the network-check explanation the player read, and
///   the record of the preferences projection the game reads. Servers are
///   named by server id and their last-known name only. The domain never
///   stores an address there: a hand-typed address is typed again.
/// - `known-hosts.json`: the domain's `topiaforge.known-hosts/1` record, which
///   holds hashes of typed addresses, never the addresses.
///
/// Every write replaces its file atomically, and the writes of one store
/// run one after another, so a slow save never lands after a newer one. A
/// damaged file is never fatal: a copy of it is kept as
/// `<name>.damaged-<UTC time>.json`, the newest [keptDamagedCopies] kept, and
/// what was valid in it, or the defaults, then take its place. Until they
/// are written the damaged file stays where it is, so a write that fails or
/// stops part way never leaves the defaults in place of what was salvaged.
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
/// backup cannot be moved, or the folder cannot be listed or searched, the
/// file counts as one that cannot be read.
///
/// A write stopped part way can also leave its `<name>.<token>.tmp`. Each
/// store sweeps those once, before its first read or write, but only once
/// they are [staleTemporaryAge] old and never while a write in this isolate
/// uses one, so a write still running elsewhere keeps its own. The token is
/// `<process id>-<microseconds>-<random>`, and only names the store gives its
/// own files are ever put back, swept or dropped: a copy the player keeps
/// beside a file or a damaged copy under another name is never touched.
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
  final _settingsChanges = StreamController<MultiplayerSettings>.broadcast();
  Future<void> _tail = Future.value();
  var _swept = false;

  /// Every settings value this store has just read or written. Settings that
  /// cannot be read never arrive here, so nothing that follows them, such as
  /// link registration, changes to match the stand-ins.
  Stream<MultiplayerSettings> get settingsChanges => _settingsChanges.stream;

  File get _settingsFile => File(p.join(directory, settingsFileName));
  File get _knownHostsFile => File(p.join(directory, knownHostsFileName));

  /// Reads the settings, recovering from any damage. Never throws: while the
  /// file cannot be read, gives [unreadableSettings] and
  /// [MultiplayerStoreRecovery.unreadable].
  Future<MultiplayerSettingsLoad> loadSettings() => _serial(() async {
    final load = await _loadSettings();
    if (load.recovery != MultiplayerStoreRecovery.unreadable) {
      _settingsChanges.add(load.settings);
    }
    return load;
  });

  /// Writes [settings], keeping what the file holds beside them. Throws
  /// [MultiplayerStoreException] when they cannot be written, and while the
  /// stored settings cannot be read, which are then left as they are.
  Future<MultiplayerSettings> saveSettings(MultiplayerSettings settings) =>
      _serial(() async {
        final current = await _readableSettings();
        await _writeSettings(settings, current.extras);
        return settings;
      });

  /// Reads the settings, applies [change] and writes the result, with no
  /// other save in between. Throws [MultiplayerStoreException] as
  /// [saveSettings] does, before [change] runs when they cannot be read.
  Future<MultiplayerSettings> updateSettings(
    MultiplayerSettings Function(MultiplayerSettings current) change,
  ) => _serial(() async {
    final current = await _readableSettings();
    final next = change(current.settings);
    await _writeSettings(next, current.extras);
    return next;
  });

  /// Runs [body] with the stored settings and a [write] that replaces them
  /// and what the file keeps beside them, with no other read or write of the
  /// settings in between, as a preferences round needs: it reads the game's
  /// journal, saves, and writes the game's copy before anything else changes
  /// the settings. [write] may be called only while [body] runs. Throws
  /// [MultiplayerStoreException] before [body] runs while the settings
  /// cannot be read, and from [write] when they cannot be written.
  Future<T> withSettings<T>(
    Future<T> Function(
      MultiplayerSettingsLoad current,
      Future<void> Function(
        MultiplayerSettings settings,
        MultiplayerSettingsExtras extras,
      )
      write,
    )
    body,
  ) => _serial(() async {
    final current = await _readableSettings();
    var open = true;
    try {
      return await body(current, (settings, extras) {
        if (!open) {
          throw StateError('The settings were written after the round ended.');
        }
        return _writeSettings(settings, extras);
      });
    } finally {
      open = false;
    }
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
          return _replaceDamaged(file, null, bytes);
        }
        if (salvaged == null) return _replaceDamaged(file, null, bytes);
        if (salvaged.damaged) {
          return _replaceDamaged(file, salvaged, bytes);
        }
        return MultiplayerSettingsLoad(
          salvaged.settings,
          MultiplayerStoreRecovery.none,
          extras: salvaged.extras,
        );
    }
  }

  /// Keeps a damaged settings file aside and writes [kept], or the defaults,
  /// in its place. What was [read] is copied aside, and the file stays in
  /// place until what replaces it is written, so a write that fails or stops
  /// part way leaves it to be salvaged again; a file that could not be read,
  /// such as a link or a folder in its place, is moved aside. Failing to do
  /// either still yields usable settings.
  Future<MultiplayerSettingsLoad> _replaceDamaged(
    File file,
    SalvagedMultiplayerSettings? kept, [
    List<int>? read,
  ]) async {
    final settings = kept?.settings ?? MultiplayerSettings();
    final extras = kept?.extras ?? MultiplayerSettingsExtras.none;
    final recovery = kept == null
        ? MultiplayerStoreRecovery.reset
        : MultiplayerStoreRecovery.salvaged;
    final keptAside = read == null
        ? await _files.setAside(file)
        : await _files.keepCopy(file, read);
    if (keptAside) {
      try {
        await _writeSettings(settings, extras);
      } on MultiplayerStoreException {
        // The next read salvages it again; these settings are in use
        // meanwhile.
      }
    }
    return MultiplayerSettingsLoad(settings, recovery, extras: extras);
  }

  /// The stored settings, recovered from any damage; throws while they
  /// cannot be read, so nothing is written over them.
  Future<MultiplayerSettingsLoad> _readableSettings() async {
    final load = await _loadSettings();
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      throw _unreadable('multiplayer settings');
    }
    return load;
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
        // The domain reads any layout and keeps what is valid. A file that
        // does not read back as its canonical bytes, damaged or only laid out
        // another way, is kept aside as a copy for inspection, and stays in
        // place until the canonical record that replaces it is written.
        final hosts = KnownHosts.decode(bytes);
        final encoded = hosts.encode();
        if (!_sameBytes(encoded, bytes) && await _files.keepCopy(file, bytes)) {
          try {
            await _files.write(file, encoded, 'known hosts');
          } on MultiplayerStoreException {
            // The next read salvages it again.
          }
        }
        return hosts;
    }
  }

  Future<void> _writeSettings(
    MultiplayerSettings settings,
    MultiplayerSettingsExtras extras,
  ) async {
    final document = {...settings.toJson(), ...extras.toJson()};
    await _files.write(
      _settingsFile,
      utf8.encode('${const JsonEncoder.withIndent('  ').convert(document)}\n'),
      'multiplayer settings',
    );
    _settingsChanges.add(settings);
  }
}
