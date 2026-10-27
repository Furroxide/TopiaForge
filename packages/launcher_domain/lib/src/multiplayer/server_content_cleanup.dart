part of '../multiplayer.dart';

/// Why a version of server content stays when a cleanup runs.
enum ServerContentKept {
  /// A profile that is not a server profile loads it or pins it.
  usedByProfile,

  /// A server profile the cleanup keeps pins it.
  pinnedByKeptServer,
}

/// One installed version that only joins brought in, as a cleanup sees it.
final class ServerContentVersion {
  ServerContentVersion({
    required this.modId,
    required this.version,
    required this.sha256,
    required this.receiptSha256,
    required this.sizeBytes,
    required Iterable<String> serverProfileIds,
    this.kept,
  }) : serverProfileIds = List.unmodifiable(
         serverProfileIds.toSet().toList()..sort(),
       ) {
    if (sizeBytes < 0) throw RangeError.value(sizeBytes, 'sizeBytes');
  }

  final String modId;
  final String version;
  final String sha256;
  final String receiptSha256;

  /// The bytes its folder holds on disk.
  final int sizeBytes;

  /// The server profiles that pin it, sorted.
  final List<String> serverProfileIds;

  /// Why it stays, or null when the cleanup removes it.
  final ServerContentKept? kept;

  bool get removable => kept == null;

  Map<String, Object?> _fingerprint() => {
    'id': modId,
    'version': version,
    'sha256': sha256,
    'receipt': receiptSha256,
    'size': sizeBytes,
    'servers': serverProfileIds,
    'kept': kept?.name,
  };
}

/// A server profile a cleanup removes along with the content only it needs.
final class ServerContentProfile {
  const ServerContentProfile({
    required this.id,
    required this.name,
    required this.revision,
  });

  final String id;
  final String name;

  /// The revision the plan saw. A profile changed since is not removed.
  final int revision;
}

/// What a cleanup of one game install's server content would remove: each
/// version with its size and the server profiles that pin it, and the server
/// profiles that go with it. `MultiplayerContentHost.planServerContentCleanup`
/// makes one, the player confirms it, and
/// `MultiplayerContentHost.applyServerContentCleanup` applies exactly it,
/// refusing it once anything it was made from has changed ([fingerprint]).
///
/// A version is removable only when no profile that is not a server profile
/// loads or pins it, and every server profile that pins it goes too; so a
/// version no server profile pins any more, such as one a server replaced,
/// goes in every cleanup. The player's own installs never appear: only
/// versions a join installed, whose install receipt is still the one the join
/// wrote, are server content. The launcher removes versions nothing pins on
/// its own after each join, so a cleanup the player asks for is about whole
/// servers.
final class ServerContentCleanupPlan {
  ServerContentCleanupPlan({
    required Iterable<ServerContentVersion> versions,
    required Iterable<ServerContentProfile> profiles,
    Set<String>? scope,
  }) : versions = List.unmodifiable(
         versions.toList()..sort((left, right) {
           final byId = left.modId.toLowerCase().compareTo(
             right.modId.toLowerCase(),
           );
           return byId != 0 ? byId : left.version.compareTo(right.version);
         }),
       ),
       profiles = List.unmodifiable(
         profiles.toList()..sort((left, right) => left.id.compareTo(right.id)),
       ),
       scope = scope == null ? null : Set.unmodifiable(scope);

  /// Every version of server content in the install, removable or kept.
  final List<ServerContentVersion> versions;

  /// The server profiles the cleanup removes.
  final List<ServerContentProfile> profiles;

  /// The server profiles the cleanup was asked to remove; null for all of
  /// them. An empty scope removes only versions no server profile pins.
  final Set<String>? scope;

  List<ServerContentVersion> get removable => [
    for (final version in versions)
      if (version.removable) version,
  ];

  int get removableBytes =>
      removable.fold(0, (total, version) => total + version.sizeBytes);

  /// Whether applying the plan would change nothing.
  bool get isEmpty => removable.isEmpty && profiles.isEmpty;

  /// Identifies everything the plan acts on: each version's archive, install
  /// receipt, size, pins and decision, each profile's revision, and the
  /// scope. Two plans with the same fingerprint do the same thing.
  String get fingerprint => PureSha256.hex(
    CanonicalJson.encode({
      'versions': [for (final version in versions) version._fingerprint()],
      'profiles': [
        for (final profile in profiles)
          {'id': profile.id, 'revision': profile.revision},
      ],
      'scope': scope == null ? null : ([...scope!]..sort()),
    }),
  );
}

/// Why a cleanup was not applied.
enum ServerContentCleanupRefusal {
  /// Something the plan was made from changed; plan again.
  stale,

  /// The game is running. A running game holds its mods' files and writes
  /// its manager state when it quits.
  gameRunning,

  /// A launch or an install is using the game's files just now.
  busy,

  /// The record of server content, the manager state or the profiles could
  /// not be read just now.
  unreadable,
}

/// What applying a cleanup did.
final class ServerContentCleanupResult {
  /// The plan was applied. [message] says what could not be tidied, if
  /// anything: a step after the content was removed that failed, such as a
  /// server profile that could not be removed, leaves nothing a profile of
  /// the player's own loads.
  const ServerContentCleanupResult.applied({
    required this.removedVersions,
    required this.removedProfiles,
    required this.freedBytes,
    this.message = '',
  }) : refusal = null;

  const ServerContentCleanupResult.refused(
    ServerContentCleanupRefusal this.refusal,
    this.message,
  ) : removedVersions = const [],
      removedProfiles = const [],
      freedBytes = 0;

  /// Why nothing was removed, or null when the plan was applied.
  final ServerContentCleanupRefusal? refusal;

  /// For people; names no address.
  final String message;

  /// `id@version` of each removed version.
  final List<String> removedVersions;

  /// The ids of the removed server profiles.
  final List<String> removedProfiles;

  /// The bytes the removed versions held. Shared downloaded archives stay
  /// cached because another install or host may still need them.
  final int freedBytes;

  bool get applied => refusal == null;
}
