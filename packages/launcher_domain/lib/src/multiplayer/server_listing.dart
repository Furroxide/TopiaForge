part of '../multiplayer.dart';

/// Where a server browser row was found.
enum ServerListingSource {
  /// The control plane's public directory.
  internet,

  /// A LAN beacon and the host's session info.
  lan,
}

/// Whether the launcher could join a listed server as things stand.
enum ServerReadiness {
  /// Not checked yet: no verified descriptor has been planned.
  unknown,

  /// Every package is installed at the exact archive.
  ready,

  /// Joinable after installing packages, from enabled sources or, in
  /// developer mode, from the host.
  needsDownloads,

  /// Something blocks the join: the game build, the runtime, content or
  /// admission.
  blocked;

  /// The readiness a join plan implies.
  static ServerReadiness of(JoinPlan plan) {
    if (!plan.isJoinable) return ServerReadiness.blocked;
    return plan.needsDownloads
        ? ServerReadiness.needsDownloads
        : ServerReadiness.ready;
  }
}

/// One row of the server browser: what a player needs to choose a server.
///
/// It never holds an address. The data layer that found a server keeps
/// whatever it needs to reach it, keyed by [serverId]; the row carries the
/// server id, names, counts, flags, ping and readiness only. Nothing in it is
/// trusted until the descriptor named by [serverId] has been verified.
final class ServerListing {
  ServerListing({
    required this.serverId,
    required this.name,
    required this.source,
    required this.players,
    required this.maxPlayers,
    required this.passwordRequired,
    required this.voice,
    this.gamemode,
    this.world,
    this.gameBuild,
    this.runtimeVersion,
    this.descriptorSha256,
    this.pingMilliseconds,
    this.readiness = ServerReadiness.unknown,
  }) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(serverId, 'serverId', 'Not a server id.');
    }
    if (descriptorSha256 != null && !WireText.isHex(descriptorSha256, 32)) {
      throw ArgumentError.value(
        descriptorSha256,
        'descriptorSha256',
        'Not a SHA-256 digest.',
      );
    }
    if (!WireText.isDisplayText(name, ServerDescriptorV1.maximumNameLength)) {
      throw ArgumentError.value(name, 'name', 'Not a server name.');
    }
    for (final (field, value) in [('gamemode', gamemode), ('world', world)]) {
      if (value != null &&
          !WireText.isDisplayText(
            value,
            ServerDescriptorV1.maximumTargetNameLength,
          )) {
        throw ArgumentError.value(value, field, 'Not a $field name.');
      }
    }
    if (gameBuild != null && !ServerDescriptorV1.isGameBuild(gameBuild)) {
      throw ArgumentError.value(gameBuild, 'gameBuild', 'Not a game build.');
    }
    if (runtimeVersion != null &&
        !ServerDescriptorV1.isSemanticVersion(runtimeVersion)) {
      throw ArgumentError.value(
        runtimeVersion,
        'runtimeVersion',
        'Not a semantic version.',
      );
    }
    if (maxPlayers < ServerDescriptorV1.minimumPlayers ||
        maxPlayers > ServerDescriptorV1.maximumPlayers) {
      throw RangeError.range(
        maxPlayers,
        ServerDescriptorV1.minimumPlayers,
        ServerDescriptorV1.maximumPlayers,
        'maxPlayers',
      );
    }
    if (players < 0 || players > maxPlayers) {
      throw RangeError.range(players, 0, maxPlayers, 'players');
    }
    final ping = pingMilliseconds;
    if (ping != null && (ping < 0 || ping > maximumPingMilliseconds)) {
      throw RangeError.range(
        ping,
        0,
        maximumPingMilliseconds,
        'pingMilliseconds',
      );
    }
  }

  /// A LAN row from a host's session info, with the round trip the browser
  /// measured for the info query.
  factory ServerListing.fromSessionInfo(
    SessionInfo info, {
    int? pingMilliseconds,
  }) => ServerListing(
    serverId: info.serverId,
    name: info.name,
    source: ServerListingSource.lan,
    players: info.players,
    maxPlayers: info.maxPlayers,
    passwordRequired: info.passwordRequired,
    voice: info.voice,
    gamemode: info.gamemode,
    world: info.world,
    gameBuild: info.gameBuild,
    runtimeVersion: info.runtimeVersion,
    descriptorSha256: info.descriptorSha256,
    pingMilliseconds: pingMilliseconds,
  );

  /// Pings past this are shown as timed out rather than stored.
  static const maximumPingMilliseconds = 60000;

  final String serverId;
  final String name;
  final ServerListingSource source;
  final int players;
  final int maxPlayers;
  final bool passwordRequired;
  final bool voice;
  final String? gamemode;
  final String? world;
  final String? gameBuild;
  final String? runtimeVersion;

  /// The hash of the signed descriptor the listing named, when it named one.
  final String? descriptorSha256;
  final int? pingMilliseconds;
  final ServerReadiness readiness;

  bool get isFull => players >= maxPlayers;

  ServerListing withReadiness(ServerReadiness readiness) =>
      _copy(readiness: readiness, pingMilliseconds: pingMilliseconds);

  ServerListing withPing(int? pingMilliseconds) =>
      _copy(readiness: readiness, pingMilliseconds: pingMilliseconds);

  ServerListing _copy({
    required ServerReadiness readiness,
    required int? pingMilliseconds,
  }) => ServerListing(
    serverId: serverId,
    name: name,
    source: source,
    players: players,
    maxPlayers: maxPlayers,
    passwordRequired: passwordRequired,
    voice: voice,
    gamemode: gamemode,
    world: world,
    gameBuild: gameBuild,
    runtimeVersion: runtimeVersion,
    descriptorSha256: descriptorSha256,
    pingMilliseconds: pingMilliseconds,
    readiness: readiness,
  );
}
