import 'dart:convert';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

final class CliServersOptions {
  CliServersOptions(List<String> args) {
    final values = <String, String>{};
    for (var i = 0; i < args.length; i++) {
      final flag = args[i];
      if (flag == '--json' && !json) {
        json = true;
      } else if (const {
            '--source',
            '--game-build',
            '--protocol-major',
            '--after',
            '--wait-seconds',
          }.contains(flag) &&
          !values.containsKey(flag) &&
          i + 1 < args.length &&
          !args[i + 1].startsWith('--')) {
        values[flag] = args[++i];
      } else {
        throw const FormatException(
          'Unknown, duplicate or incomplete servers option. Run topiaforge servers --help.',
        );
      }
    }
    source = values['--source'] ?? 'all';
    if (!const {'all', 'internet', 'lan'}.contains(source)) {
      throw const FormatException('Use --source all|internet|lan.');
    }
    gameBuild = values['--game-build'];
    protocolMajor = int.tryParse(values['--protocol-major'] ?? '1') ?? -1;
    final seconds = int.tryParse(values['--wait-seconds'] ?? '3') ?? -1;
    after = values['--after'];
    if ((gameBuild != null && !ServerDescriptorV1.isGameBuild(gameBuild)) ||
        protocolMajor < 0 ||
        protocolMajor > ServerBrowseFilter.maximumProtocolMajor ||
        seconds < 0 ||
        seconds > 30 ||
        (after != null && !RegExp(r'^[0-9a-f]{64}$').hasMatch(after!)) ||
        (source == 'lan' && after != null)) {
      throw const FormatException(
        'Invalid browser filter. Use a valid build, protocol, server cursor and --wait-seconds 0..30.',
      );
    }
    wait = Duration(seconds: seconds);
  }
  bool json = false;
  late final String source;
  late final String? gameBuild, after;
  late final int protocolMajor;
  late final Duration wait;
}

/// A bounded browser snapshot: one Internet page and a timed LAN discovery.
/// JSON is versioned and contains only the address-free listing projection.
Future<int> runCliServers({
  required MultiplayerRepository repository,
  required CliServersOptions options,
  required void Function(String) write,
  required void Function(String) notice,
  Future<void> Function(Duration)? delay,
  Future<String?> Function()? resolveGameBuild,
}) async {
  final rows = <ServerListing>[];
  final statuses = <String, Object?>{};
  String? next;
  var available = false;
  if (options.source != 'lan') {
    try {
      if (repository.serviceStatus != MultiplayerServiceStatus.configured) {
        statuses['internet'] = 'notConfigured';
      } else {
        final build = options.gameBuild ?? await resolveGameBuild?.call();
        if (build == null) {
          statuses['internet'] = 'buildRequired';
        } else {
          final page = await repository.browseInternet(
            ServerBrowseFilter(
              gameBuild: build,
              protocolMajor: options.protocolMajor,
            ),
            after: options.after,
          );
          statuses['internet'] = page.isAvailable
              ? 'available'
              : page.problem?.name ?? 'unavailable';
          statuses['retryAfterSeconds'] = page.retryAfter?.inSeconds;
          if (page.isAvailable) {
            available = true;
            rows.addAll(page.listings);
            next = page.next;
          }
        }
      }
    } on Object {
      statuses['internet'] = 'unavailable';
    }
  }
  if (options.source != 'internet') {
    try {
      notice(
        'LAN discovery listens for local sessions. Windows may ask for firewall access; allow private networks only. Turn discovery off in Multiplayer settings.',
      );
      final start = await repository.startLanBrowsing();
      if (start.status == LanDiscoveryStatus.listening) {
        await (delay ?? Future<void>.delayed)(options.wait);
      }
      final snapshot = repository.lanSnapshot;
      statuses['lan'] = snapshot.problem?.name ?? snapshot.status.name;
      if (snapshot.status == LanDiscoveryStatus.listening) {
        available = true;
        rows.addAll(
          snapshot.listings.where(
            (row) =>
                options.gameBuild == null || row.gameBuild == options.gameBuild,
          ),
        );
      }
    } on Object {
      statuses['lan'] = 'unavailable';
    } finally {
      try {
        await repository.stopLanBrowsing();
      } on Object {
        statuses['lanCleanup'] = 'unavailable';
        notice(
          'LAN discovery cleanup was unconfirmed. Close this command before retrying.',
        );
      }
    }
  }
  rows.sort((a, b) {
    final source = a.source.name.compareTo(b.source.name);
    return source != 0 ? source : a.serverId.compareTo(b.serverId);
  });
  final unique = <String>{};
  final servers = rows
      .where((row) => unique.add('${row.source.name}:${row.serverId}'))
      .toList();
  if (options.json) {
    write(
      jsonEncode({
        'schemaVersion': 1,
        'sources': statuses,
        'next': next,
        'servers': [
          for (final row in servers)
            {
              'serverId': row.serverId,
              'name': row.name,
              'source': row.source.name,
              'players': row.players,
              'maxPlayers': row.maxPlayers,
              'passwordRequired': row.passwordRequired,
              'voice': row.voice,
              'gamemode': row.gamemode,
              'world': row.world,
              'gameBuild': row.gameBuild,
              'runtimeVersion': row.runtimeVersion,
              'pingMilliseconds': row.pingMilliseconds,
              'readiness': row.readiness.name,
            },
        ],
      }),
    );
  } else {
    write('SOURCE    PLAYERS  VOICE  PASSWORD  NAME / SERVER ID');
    for (final row in servers) {
      write(
        '${row.source.name.padRight(10)}${'${row.players}/${row.maxPlayers}'.padRight(9)}'
        '${(row.voice ? 'on' : 'off').padRight(7)}${(row.passwordRequired ? 'yes' : 'no').padRight(10)}'
        '${row.name} / ${row.serverId}',
      );
    }
    if (servers.isEmpty) write('No sessions found in this snapshot.');
    for (final entry in statuses.entries) {
      write('${entry.key}: ${entry.value ?? 'none'}');
    }
    if (next != null) {
      write('More Internet sessions: rerun with --after $next.');
    }
  }
  if (statuses['internet'] == 'buildRequired') {
    notice(
      'Internet browsing needs --game-build <build> from the game you want to play.',
    );
  }
  if (!available) {
    notice(
      'No browser source was available. Check Multiplayer settings, your connection and the build filter, then retry.',
    );
  }
  return available ? 0 : 1;
}
