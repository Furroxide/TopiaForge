import 'dart:convert';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_servers.dart';

void main() {
  late _Browser repository;
  late List<String> output, notices;
  setUp(() {
    repository = _Browser();
    output = [];
    notices = [];
  });
  Future<int> run(List<String> args) => runCliServers(
    repository: repository,
    options: CliServersOptions(args),
    write: output.add,
    notice: notices.add,
    delay: (_) async {},
  );
  test(
    'JSON is one versioned document with stable address-free fields',
    () async {
      expect(await run(['--json', '--game-build', '2545']), 0);
      final json = jsonDecode(output.single) as Map;
      expect(json['schemaVersion'], 1);
      expect(
        json.keys,
        unorderedEquals([
          'schemaVersion',
          'sources',
          'retryAfterSeconds',
          'next',
          'servers',
        ]),
      );
      expect(json['sources'], {'internet': 'available', 'lan': 'listening'});
      expect(json['retryAfterSeconds'], isNull);
      expect(json['next'], 'c' * 64);
      final rows = json['servers'] as List;
      expect(rows.length, 2);
      expect(rows[0]['source'], 'internet');
      expect(rows[1]['source'], 'lan');
      expect(
        rows.first.keys,
        unorderedEquals([
          'serverId',
          'name',
          'source',
          'players',
          'maxPlayers',
          'passwordRequired',
          'voice',
          'gamemode',
          'world',
          'gameBuild',
          'runtimeVersion',
          'pingMilliseconds',
          'readiness',
        ]),
      );
      expect(repository.stops, 1);
    },
  );
  test('table has player counts, flags and cursor', () async {
    expect(await run(['--game-build', '2545']), 0);
    expect(output.first, contains('PLAYERS'));
    expect(output.join('\n'), contains('1/8'));
    expect(output.last, contains('--after'));
  });
  test(
    'Internet filters and cursor reach repository without LAN sockets',
    () async {
      await run([
        '--source',
        'internet',
        '--game-build',
        '2545',
        '--protocol-major',
        '2',
        '--after',
        'a' * 64,
      ]);
      expect(repository.filter!.protocolMajor, 2);
      expect(repository.after, 'a' * 64);
      expect(repository.starts, 0);
      expect(repository.stops, 0);
    },
  );
  test('unconfigured Internet still lists LAN', () async {
    repository.configured = false;
    expect(await run(['--json']), 0);
    expect(repository.browses, 0);
    expect(
      (jsonDecode(output.single) as Map)['sources']['internet'],
      'notConfigured',
    );
  });
  test(
    'configured Internet requires explicit build without discovery',
    () async {
      expect(await run(['--source', 'internet', '--json']), 1);
      expect(repository.browses, 0);
      expect(notices.join(), contains('--game-build'));
    },
  );
  test('LAN disabled is honored with machine-readable state', () async {
    repository.lan = LanBrowseSnapshot(status: LanDiscoveryStatus.disabled);
    expect(await run(['--source', 'lan', '--json']), 1);
    expect((jsonDecode(output.single) as Map)['sources']['lan'], 'disabled');
    expect(repository.stops, 1);
  });
  test('Internet exception is redacted and does not suppress LAN', () async {
    repository.throws = true;
    expect(await run(['--json', '--game-build', '2545']), 0);
    expect(output.join(), isNot(contains('private endpoint')));
    expect(repository.starts, 1);
    expect(repository.stops, 1);
  });
  test('LAN failure preserves an available Internet page', () async {
    repository.failLan = true;
    expect(await run(['--json', '--game-build', '2545']), 0);
    final result = jsonDecode(output.single) as Map;
    expect(result['sources']['lan'], 'unavailable');
    expect(result['servers'], hasLength(1));
    expect(repository.stops, 1);
  });
  test('LAN cleanup failure still emits redacted JSON', () async {
    repository.failStop = true;
    expect(await run(['--json', '--source', 'lan']), 0);
    expect((jsonDecode(output.single) as Map)['sources'], {'lan': 'listening'});
    expect(notices.join(), contains('cleanup was unconfirmed'));
    expect([...output, ...notices].join(), isNot(contains('private endpoint')));
  });
  test('a service delay is a retry hint, never a source status', () async {
    repository.page = const InternetBrowseResult.unavailable(
      'Busy.',
      problem: MultiplayerServiceProblem.rateLimited,
      retryAfter: Duration(seconds: 30),
    );
    expect(await run(['--json', '--game-build', '2545']), 0);
    final json = jsonDecode(output.single) as Map;
    expect(json['sources'], {'internet': 'rateLimited', 'lan': 'listening'});
    expect(json['retryAfterSeconds'], 30);
    output.clear();
    expect(await run(['--game-build', '2545']), 0);
    expect(output, contains('internet: rateLimited'));
    expect(output, contains('Internet browsing can be retried in 30 seconds.'));
    expect(output.join('\n'), isNot(contains('retryAfterSeconds')));
  });
  test('table shows no retry line when the service named no delay', () async {
    expect(await run(['--game-build', '2545']), 0);
    expect(
      output,
      containsAllInOrder(['internet: available', 'lan: listening']),
    );
    expect(output.join('\n'), isNot(contains('retry')));
  });
  test('empty available browser is a successful snapshot', () async {
    repository.page = InternetBrowseResult.page([]);
    expect(await run(['--source', 'internet', '--game-build', '2545']), 0);
    expect(output, contains('No sessions found in this snapshot.'));
  });
  test('detected game build supplies the default Internet filter', () async {
    expect(
      await runCliServers(
        repository: repository,
        options: CliServersOptions(['--source', 'internet']),
        write: output.add,
        notice: notices.add,
        resolveGameBuild: () async => '2545',
      ),
      0,
    );
    expect(repository.filter!.gameBuild, '2545');
  });
  for (final args in [
    ['--json', '--json'],
    ['--source', 'bad'],
    ['--wait-seconds', '31'],
    ['--after', 'not-a-server'],
    ['--game-build'],
    ['--protocol-major', '-1'],
  ]) {
    test('invalid browser arguments ${args.first}', () {
      expect(() => CliServersOptions(args), throwsFormatException);
    });
  }
}

class _Browser implements MultiplayerRepository {
  bool configured = true, throws = false, failLan = false, failStop = false;
  int starts = 0, stops = 0, browses = 0;
  ServerBrowseFilter? filter;
  String? after;
  InternetBrowseResult page = InternetBrowseResult.page([
    _row('b', ServerListingSource.internet),
  ], next: 'c' * 64);
  LanBrowseSnapshot lan = LanBrowseSnapshot(
    status: LanDiscoveryStatus.listening,
    listings: [_row('a', ServerListingSource.lan)],
  );
  @override
  MultiplayerServiceStatus get serviceStatus => configured
      ? MultiplayerServiceStatus.configured
      : MultiplayerServiceStatus.unconfigured;
  @override
  Future<InternetBrowseResult> browseInternet(
    ServerBrowseFilter value, {
    String? after,
    CancellationToken? cancellation,
  }) async {
    browses++;
    filter = value;
    this.after = after;
    if (throws) throw StateError('private endpoint');
    return page;
  }

  @override
  Future<LanBrowseSnapshot> startLanBrowsing() async {
    starts++;
    if (failLan) throw StateError('private endpoint');
    return lan;
  }

  @override
  LanBrowseSnapshot get lanSnapshot => lan;
  @override
  Future<LanBrowseSnapshot> stopLanBrowsing() async {
    stops++;
    if (failStop) throw StateError('private endpoint');
    return lan;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

ServerListing _row(String id, ServerListingSource source) => ServerListing(
  serverId: id * 64,
  name: 'Sam',
  source: source,
  players: 1,
  maxPlayers: 8,
  passwordRequired: false,
  voice: false,
  gameBuild: '2545',
);
