/// Reading stored multiplayer settings as far as they are valid.
library;

import 'package:launcher_domain/launcher_domain.dart';

/// Settings read from a stored document, and whether anything in it had to
/// be dropped.
typedef SalvagedMultiplayerSettings = ({
  MultiplayerSettings settings,
  bool damaged,
});

/// Reads [document] member by member, keeping every member that is valid and
/// dropping the rest, so one bad bookmark never costs the player their other
/// settings. Bookmarks that do not read, repeat a server or pass the list's
/// bound are dropped one by one. Unknown members are ignored, as
/// [MultiplayerSettings.fromJson] ignores them.
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
  try {
    return (settings: MultiplayerSettings.fromJson(clean), damaged: damaged);
  } on FormatException {
    return null;
  }
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
