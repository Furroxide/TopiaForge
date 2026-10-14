part of '../multiplayer.dart';

/// A server the player saved or joined, by server id and last-known name.
///
/// Bookmarks never hold an address, so the settings file that keeps them can
/// travel in a diagnostic bundle. Rejoining goes through the control plane
/// or LAN discovery by server id; a hand-typed address is typed again.
final class ServerBookmark {
  ServerBookmark({
    required this.serverId,
    required this.name,
    required DateTime savedAt,
  }) : savedAt = savedAt.toUtc() {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(serverId, 'serverId', 'Not a server id.');
    }
    if (!WireText.isDisplayText(name, ServerDescriptorV1.maximumNameLength)) {
      throw ArgumentError.value(name, 'name', 'Not a server name.');
    }
  }

  factory ServerBookmark.fromJson(Object? value) {
    if (value is! Map<String, Object?>) {
      throw const FormatException('A server bookmark is an object.');
    }
    final savedAt = DateTime.tryParse(_requireString(value, 'savedAt'));
    if (savedAt == null) {
      throw const FormatException('A bookmark time is an ISO 8601 date.');
    }
    try {
      return ServerBookmark(
        serverId: _requireString(value, 'serverId'),
        name: _requireString(value, 'name'),
        savedAt: savedAt,
      );
    } on ArgumentError catch (error) {
      throw FormatException('A server bookmark is invalid: ${error.message}');
    }
  }

  final String serverId;
  final String name;

  /// When the server was saved, or last joined, in UTC.
  final DateTime savedAt;

  Map<String, Object?> toJson() => {
    'serverId': serverId,
    'name': name,
    'savedAt': savedAt.toIso8601String(),
  };
}

/// The player's favorite servers, newest first, each once, at most
/// [maximumEntries].
final class ServerFavorites {
  ServerFavorites([Iterable<ServerBookmark> entries = const []])
    : entries = List.unmodifiable(entries) {
    _requireBounded(this.entries, maximumEntries, 'favorites');
  }

  static const maximumEntries = 100;

  final List<ServerBookmark> entries;

  bool get isFull => entries.length >= maximumEntries;

  bool contains(String serverId) =>
      entries.any((entry) => entry.serverId == serverId);

  /// Adds [bookmark] first, or refreshes its name and time if it is already
  /// a favorite. Throws [StateError] when the list is full; favorites are
  /// never dropped silently.
  ServerFavorites add(ServerBookmark bookmark) {
    final others = entries.where((item) => item.serverId != bookmark.serverId);
    if (others.length >= maximumEntries) {
      throw StateError('At most $maximumEntries favorites can be kept.');
    }
    return ServerFavorites([bookmark, ...others]);
  }

  ServerFavorites remove(String serverId) =>
      ServerFavorites(entries.where((entry) => entry.serverId != serverId));

  List<Object?> toJson() => [for (final entry in entries) entry.toJson()];

  factory ServerFavorites.fromJson(Object? value) =>
      ServerFavorites(_readBookmarks(value, maximumEntries, 'favorites'));
}

/// Servers the player joined, most recent first, each once, at most
/// [maximumEntries]. Recording a join past the bound forgets the oldest.
final class ServerHistory {
  ServerHistory([Iterable<ServerBookmark> entries = const []])
    : entries = List.unmodifiable(entries) {
    _requireBounded(this.entries, maximumEntries, 'history');
  }

  static const maximumEntries = 50;

  final List<ServerBookmark> entries;

  /// Moves [joined] to the front, replacing any earlier entry for the same
  /// server.
  ServerHistory record(ServerBookmark joined) => ServerHistory(
    [
      joined,
      ...entries.where((entry) => entry.serverId != joined.serverId),
    ].take(maximumEntries),
  );

  ServerHistory remove(String serverId) =>
      ServerHistory(entries.where((entry) => entry.serverId != serverId));

  ServerHistory clear() => ServerHistory();

  List<Object?> toJson() => [for (final entry in entries) entry.toJson()];

  factory ServerHistory.fromJson(Object? value) =>
      ServerHistory(_readBookmarks(value, maximumEntries, 'history'));
}

void _requireBounded(List<ServerBookmark> entries, int maximum, String name) {
  if (entries.length > maximum) {
    throw ArgumentError.value(
      entries.length,
      name,
      'At most $maximum entries are kept.',
    );
  }
  final seen = <String>{};
  for (final entry in entries) {
    if (!seen.add(entry.serverId)) {
      throw ArgumentError.value(
        entry.serverId,
        name,
        'Each server appears once.',
      );
    }
  }
}

List<ServerBookmark> _readBookmarks(Object? value, int maximum, String name) {
  if (value is! List<Object?> || value.length > maximum) {
    throw FormatException('The $name list holds at most $maximum bookmarks.');
  }
  final bookmarks = [for (final item in value) ServerBookmark.fromJson(item)];
  if (bookmarks.map((entry) => entry.serverId).toSet().length !=
      bookmarks.length) {
    throw FormatException('The $name list repeats a server.');
  }
  return bookmarks;
}
