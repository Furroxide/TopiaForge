/// Reading stored multiplayer settings as far as they are valid.
library;

import 'package:launcher_domain/launcher_domain.dart';

/// Settings read from a stored document, what the document keeps beside
/// them, and whether anything in it had to be dropped.
typedef SalvagedMultiplayerSettings = ({
  MultiplayerSettings settings,
  MultiplayerSettingsExtras extras,
  bool damaged,
});

/// What the settings file keeps beside [MultiplayerSettings]: the hosting
/// defaults, the network-check explanation the player read, and the record of
/// the preferences projection the game reads (its revision, and the
/// launcher's position in the game's journal), which a preferences round
/// saves with the settings it changes.
final class MultiplayerSettingsExtras {
  const MultiplayerSettingsExtras({
    this.hosting,
    this.networkNotice = 0,
    this.revision,
    this.journalEpoch,
    this.journalApplied = 0,
  }) : assert(networkNotice >= 0),
       assert(revision == null || revision >= 0),
       assert(journalApplied >= 0),
       assert(journalEpoch != null || journalApplied == 0);

  static const none = MultiplayerSettingsExtras();

  /// The hosting defaults, or null until any are stored: the projection then
  /// keeps its own.
  final MultiplayerHostingDefaults? hosting;

  /// The newest network-check explanation the player read; 0 when never.
  final int networkNotice;

  /// The projection revision the last round saved, or null before the first.
  final int? revision;

  /// The epoch of the journal the last round took changes in from, or null
  /// before the first; [journalApplied] counts in that journal only.
  final String? journalEpoch;
  final int journalApplied;

  /// The members as the settings file holds them, beside the settings'.
  Map<String, Object?> toJson() => {
    if (hosting case final hosting?) 'hosting': hosting.toJson(),
    if (networkNotice != 0) 'networkNotice': networkNotice,
    if (revision case final revision?)
      'projection': {
        'revision': revision,
        'journalEpoch': ?journalEpoch,
        'journalApplied': journalApplied,
      },
  };
}

/// Reads [document] member by member, keeping every member that is valid and
/// dropping the rest, so one bad bookmark never costs the player their other
/// settings. Bookmarks that do not read, repeat a server or pass the list's
/// bound are dropped one by one. What the document keeps beside the settings
/// ([MultiplayerSettingsExtras]) is read the same way. Unknown members are
/// ignored, as [MultiplayerSettings.fromJson] ignores them.
///
/// Returns null when nothing can be used: the document is not an object, or
/// it was written for another schema version, which this launcher must not
/// guess at.
SalvagedMultiplayerSettings? salvageMultiplayerSettings(Object? document) {
  if (document is! Map) return null;
  final json = <String, Object?>{
    for (final MapEntry(:key, :value) in document.entries)
      if (key is String) key: value,
  };
  final version = json['schemaVersion'];
  if (json.containsKey('schemaVersion') &&
      version != MultiplayerSettings.schemaVersion) {
    return null;
  }
  var damaged = false;
  final clean = <String, Object?>{
    'schemaVersion': MultiplayerSettings.schemaVersion,
  };
  void keep<T>(String key, [bool Function(T value)? valid]) {
    if (!json.containsKey(key)) return;
    final value = json[key];
    if (value is T && (valid == null || valid(value))) {
      clean[key] = value;
    } else {
      damaged = true;
    }
  }

  keep<String>('displayName', MultiplayerSettings.isValidDisplayName);
  keep<int>('acknowledgedPrivacyDisclosure', (value) => value >= 0);
  keep<bool>('upnpEnabled');
  keep<bool>('lanDiscoveryEnabled');
  keep<int>('voiceConsent', (value) => value >= 0);
  keep<bool>('voiceEnabled');
  keep<bool>('linkHandlingEnabled');
  for (final (key, maximum) in [
    ('favorites', ServerFavorites.maximumEntries),
    ('history', ServerHistory.maximumEntries),
  ]) {
    if (!json.containsKey(key)) continue;
    final (bookmarks, lost) = _bookmarks(json[key], maximum);
    damaged |= lost;
    clean[key] = [for (final bookmark in bookmarks) bookmark.toJson()];
  }
  final (extras, extrasLost) = _extras(json);
  try {
    return (
      settings: MultiplayerSettings.fromJson(clean),
      extras: extras,
      damaged: damaged || extrasLost,
    );
  } on FormatException {
    return null;
  }
}

/// The valid members of [json] that it keeps beside the settings, and
/// whether any was dropped. A damaged record of the projection is dropped
/// whole: the projection's own record then counts the game's journal.
(MultiplayerSettingsExtras, bool) _extras(Map<String, Object?> json) {
  var damaged = false;
  MultiplayerHostingDefaults? hosting;
  if (json.containsKey('hosting')) {
    try {
      hosting = MultiplayerHostingDefaults.fromJson(json['hosting']);
    } on FormatException {
      damaged = true;
    }
  }
  var networkNotice = 0;
  if (json.containsKey('networkNotice')) {
    final value = json['networkNotice'];
    if (value is int && value >= 0) {
      networkNotice = value;
    } else {
      damaged = true;
    }
  }
  final record = json.containsKey('projection')
      ? _projectionRecord(json['projection'])
      : null;
  if (json.containsKey('projection') && record == null) damaged = true;
  return (
    MultiplayerSettingsExtras(
      hosting: hosting,
      networkNotice: networkNotice,
      revision: record?.revision,
      journalEpoch: record?.journalEpoch,
      journalApplied: record?.journalApplied ?? 0,
    ),
    damaged,
  );
}

/// A stored record of the projection, or null when it is not one: a
/// revision and a journal position that are not negative, and a journal
/// epoch whenever the position is not 0, by the journal's own rule.
({int revision, String? journalEpoch, int journalApplied})? _projectionRecord(
  Object? value,
) {
  if (value is! Map ||
      value.keys.any(
        (key) =>
            !const {'revision', 'journalEpoch', 'journalApplied'}.contains(key),
      )) {
    return null;
  }
  final revision = value['revision'];
  final applied = value['journalApplied'];
  final epoch = value['journalEpoch'];
  if (revision is! int || revision < 0 || applied is! int || applied < 0) {
    return null;
  }
  if (epoch == null) {
    return applied == 0 && !value.containsKey('journalEpoch')
        ? (revision: revision, journalEpoch: null, journalApplied: 0)
        : null;
  }
  if (epoch is! String) return null;
  try {
    MultiplayerPreferencesJournal(epoch, const []);
  } on FormatException {
    return null;
  }
  return (revision: revision, journalEpoch: epoch, journalApplied: applied);
}

/// The valid bookmarks of [value], each server once and at most [maximum],
/// and whether any was dropped.
(List<ServerBookmark>, bool) _bookmarks(Object? value, int maximum) {
  if (value is! List) return (const [], true);
  final bookmarks = <ServerBookmark>[];
  final seen = <String>{};
  var lost = false;
  for (final item in value) {
    if (bookmarks.length == maximum) {
      lost = true;
      break;
    }
    try {
      final bookmark = ServerBookmark.fromJson(item);
      if (seen.add(bookmark.serverId)) {
        bookmarks.add(bookmark);
      } else {
        lost = true;
      }
    } on FormatException {
      lost = true;
    }
  }
  return (bookmarks, lost);
}
