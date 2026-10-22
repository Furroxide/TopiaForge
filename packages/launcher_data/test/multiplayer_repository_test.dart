import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';
import 'secure_http_loopback.dart';

void main() {
  final loopback = InternetAddress.loopbackIPv4;
  late Directory root;

  setUp(() => root = Directory.systemTemp.createTempSync('tf-mp-repo-'));
  tearDown(() => root.deleteSync(recursive: true));

  LocalMultiplayerRepository repository({
    LanDiscoveryClient? lan,
    MultiplayerServiceConfiguration? service,
  }) {
    final created = LocalMultiplayerRepository(
      dataRoot: root.path,
      service: service ?? MultiplayerServiceConfiguration.none,
      lan:
          lan ??
          LanDiscoveryClient(
            listenAddress: loopback,
            port: 0,
            queryAddress: loopback,
          ),
      diagnostics: MultiplayerNetworkDiagnostics(
        bindAddress: loopback,
        gamePort: 0,
        lanPort: 0,
      ),
      clock: () => DateTime.utc(2026, 10, 15, 12),
    );
    addTearDown(created.dispose);
    return created;
  }

  group('settings, favorites and history', () {
    test('live in the multiplayer folder of the data root', () async {
      final repo = repository();
      await repo.saveSettings(MultiplayerSettings(displayName: 'Sam'));
      final file = File(p.join(root.path, 'multiplayer', 'settings.json'));
      expect(jsonDecode(file.readAsStringSync())['displayName'], 'Sam');
      expect((await repo.loadSettings()).settings.displayName, 'Sam');
    });

    test('favorites and history name servers by id and name only', () async {
      final repo = repository();
      await repo.addFavorite(serverId: hostAServerId, name: "Sam's Sandbox");
      await repo.addFavorite(serverId: hostAServerId, name: 'Renamed');
      await repo.recordJoin(serverId: hostBServerId, name: 'Invite Only');
      await repo.recordJoin(serverId: hostAServerId, name: 'Renamed');
      var settings = (await repo.loadSettings()).settings;
      expect(settings.favorites.entries.single.name, 'Renamed');
      expect(settings.history.entries.map((entry) => entry.serverId), [
        hostAServerId,
        hostBServerId,
      ]);
      final stored =
          jsonDecode(
                File(
                  p.join(root.path, 'multiplayer', 'settings.json'),
                ).readAsStringSync(),
              )
              as Map;
      for (final list in [stored['favorites'], stored['history']]) {
        for (final entry in list as List) {
          expect((entry as Map).keys.toSet(), {'serverId', 'name', 'savedAt'});
        }
      }
      await repo.removeFavorite(hostAServerId);
      await repo.removeFromHistory(hostBServerId);
      settings = (await repo.loadSettings()).settings;
      expect(settings.favorites.entries, isEmpty);
      expect(settings.history.entries.single.serverId, hostAServerId);
      settings = await repo.clearHistory();
      expect(settings.history.entries, isEmpty);
    });

    test(
      'a full favorites list refuses another rather than drop one',
      () async {
        final repo = repository();
        for (var index = 0; index < ServerFavorites.maximumEntries; index++) {
          await repo.addFavorite(
            serverId: PureSha256.hex(utf8.encode('server $index')),
            name: 'Server $index',
          );
        }
        await expectLater(
          repo.addFavorite(serverId: hostAServerId, name: 'One too many'),
          throwsStateError,
        );
        final settings = (await repo.loadSettings()).settings;
        expect(settings.favorites.entries, hasLength(100));
      },
    );

    test('a damaged settings file is never fatal', () async {
      final folder = Directory(p.join(root.path, 'multiplayer'))
        ..createSync(recursive: true);
      File(p.join(folder.path, 'settings.json')).writeAsStringSync('{nope');
      final load = await repository().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.reset);
      expect(load.settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
    });
  });

  group('LAN browsing', () {
    test('follows the player\'s setting', () async {
      final lan = LanDiscoveryClient(
        listenAddress: loopback,
        port: 0,
        queryAddress: loopback,
      );
      final repo = repository(lan: lan);
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.listening,
      );
      final published = repo.lanSnapshots.firstWhere(
        (snapshot) => snapshot.status == LanDiscoveryStatus.disabled,
      );
      final off = (await repo.loadSettings()).settings.withLanDiscovery(false);
      final saving = repo.saveSettings(off);
      // Browsing stops before the save is even written.
      expect(lan.isListening, isFalse);
      expect(repo.lanSnapshot.status, LanDiscoveryStatus.disabled);
      await saving;
      expect(lan.isListening, isFalse);
      expect((await published).message, contains('LAN discovery is off'));
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.disabled,
      );
      expect(lan.isListening, isFalse);
      await repo.saveSettings(off.withLanDiscovery(true));
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.listening,
      );
      expect((await repo.stopLanBrowsing()).status, LanDiscoveryStatus.stopped);
    });

    group('a start still reading the settings', () {
      test('yields to turning discovery off', () async {
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        await repo.saveSettings(MultiplayerSettings());
        // The start reads settings that still say on.
        final starting = repo.startLanBrowsing();
        await repo.saveSettings(MultiplayerSettings().withLanDiscovery(false));
        await starting;
        expect(rig.binder.binds, isEmpty);
        expect(rig.client.isListening, isFalse);
        expect(repo.lanSnapshot.status, LanDiscoveryStatus.disabled);
      });

      test('yields to a stop', () async {
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        await repo.saveSettings(MultiplayerSettings());
        final starting = repo.startLanBrowsing();
        await repo.stopLanBrowsing();
        await starting;
        expect(rig.binder.binds, isEmpty);
        expect(repo.lanSnapshot.status, LanDiscoveryStatus.stopped);
      });

      test('yields to a later start, so browsing opens once', () async {
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        await repo.saveSettings(MultiplayerSettings());
        final first = repo.startLanBrowsing();
        final second = repo.startLanBrowsing();
        await first;
        expect((await second).status, LanDiscoveryStatus.listening);
        expect(rig.binder.binds, hasLength(2));
      });
    });

    group('a start called while a save is still being written', () {
      test('reads discovery turned off', () async {
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        await repo.saveSettings(MultiplayerSettings());
        final saving = repo.saveSettings(
          MultiplayerSettings().withLanDiscovery(false),
        );
        final started = await repo.startLanBrowsing();
        await saving;
        expect(rig.binder.binds, isEmpty);
        expect(rig.client.isListening, isFalse);
        expect(started.status, LanDiscoveryStatus.disabled);
      });

      test('reads discovery turned on', () async {
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        await repo.saveSettings(MultiplayerSettings().withLanDiscovery(false));
        final saving = repo.saveSettings(MultiplayerSettings());
        final started = await repo.startLanBrowsing();
        await saving;
        expect(started.status, LanDiscoveryStatus.listening);
        expect(rig.client.isListening, isTrue);
      });
    });
  });

  group('the multiplayer service', () {
    test('is named by a define that is empty by default', () {
      expect(
        MultiplayerServiceConfiguration.defineName,
        'TOPIAFORGE_CONTROL_PLANE_ORIGIN',
      );
      final compiled = MultiplayerServiceConfiguration.fromEnvironment();
      expect(compiled.status, MultiplayerServiceStatus.unconfigured);
      expect(compiled.origin, isNull);
    });

    test('accepts only an HTTPS DNS name and a port, in one spelling', () {
      for (final text in ['', '   ']) {
        expect(
          MultiplayerServiceConfiguration.parse(text).status,
          MultiplayerServiceStatus.unconfigured,
        );
      }
      for (final (text, origin) in [
        ('https://play.example', 'https://play.example'),
        ('https://play.example/', 'https://play.example'),
        ('https://play.example:8443', 'https://play.example:8443'),
        ('https://play.example:1', 'https://play.example:1'),
        ('https://play.example:65535', 'https://play.example:65535'),
        ('https://a-b.example.org:443', 'https://a-b.example.org'),
        ('https://localhost:8787', 'https://localhost:8787'),
        ('HTTPS://Play.Example/', 'https://play.example'),
        ('  https://play.example  ', 'https://play.example'),
        // A number that is not the last label, or not a number at all.
        ('https://0x7f.example.org', 'https://0x7f.example.org'),
        ('https://example.0x7g', 'https://example.0x7g'),
        ('https://123.example:8443/', 'https://123.example:8443'),
      ]) {
        final configuration = MultiplayerServiceConfiguration.parse(text);
        expect(configuration.status, MultiplayerServiceStatus.configured);
        expect(configuration.isConfigured, isTrue);
        expect(configuration.origin.toString(), origin, reason: text);
        // An origin every launch can carry, in the one spelling it carries:
        // `https://`, a lowercase DNS name, a port without a leading zero,
        // and nothing after.
        expect(LaunchSessionRequest.isServiceOrigin(origin), isTrue);
        expect(origin, matches(RegExp(r'^https://[a-z0-9.-]+(:[1-9]\d*)?$')));
      }
      for (final (about, texts) in [
        (
          'another scheme',
          [
            'http://play.example',
            'ftp://play.example',
            'play.example',
            'https:play.example',
            '//play.example',
          ],
        ),
        (
          'an IP literal',
          [
            'https://203.0.113.5',
            'https://127.0.0.1:8443',
            'https://[::1]',
            'https://[2001:db8::1]:8443',
            'https://[::ffff:203.0.113.5]',
            'https://::1',
            'https://2001:db8::1',
          ],
        ),
        (
          'a name whose last label is a number, an IPv4 address to a URL',
          [
            'https://0x7f000001',
            'HTTPS://0X7F000001/',
            'https://0x7f000001:8443',
            'https://0x',
            'https://lan.0x1f',
            'https://example.123',
            'https://play.example.42:443',
          ],
        ),
        (
          'a port out of range or not plain decimal',
          [
            'https://play.example:0',
            'https://play.example:65536',
            'https://play.example:99999',
            'https://play.example:08443',
            'https://play.example:',
            'https://play.example:-1',
            'https://play.example:84a3',
            'https://play.example:8443:1',
          ],
        ),
        (
          'a host that is not a DNS name',
          [
            'https://pl%20ay.example',
            'https://pl ay.example',
            'https://play..example',
            'https://-play.example',
            'https://play.example.',
            'https://${'a' * 64}.example',
            'https://${'a' * 300}.example',
            'https://',
            'https://:8443',
          ],
        ),
        (
          'user info',
          ['https://user:secret@play.example', 'https://user@play.example'],
        ),
        (
          'a path, query or fragment',
          [
            'https://play.example/v1',
            'https://play.example//',
            'https://play.example\\v1',
            'https://play.example?region=eu',
            'https://play.example/?region=eu',
            'https://play.example#top',
            'https://play.example/#top',
          ],
        ),
      ]) {
        for (final text in texts) {
          final configuration = MultiplayerServiceConfiguration.parse(text);
          expect(
            configuration.status,
            MultiplayerServiceStatus.invalid,
            reason: '$about: $text',
          );
          expect(configuration.origin, isNull);
        }
      }
    });

    test('reads as itself exactly the origins a launch carries', () {
      // One rule decides both: the configuration reads a text as itself
      // exactly when a launch carries that text, and every origin it reads is
      // one a launch carries.
      final texts = [
        for (final host in [
          'play.example',
          'localhost',
          '0x7f.example.org',
          'example.0x7g',
          '123.example',
          '0x7f000001',
          '0x',
          'lan.0x1f',
          'example.123',
          'play.example.42',
          '127.1',
          '203.0.113.5',
          '[::1]',
          'Play.Example',
          'play..example',
        ])
          for (final port in ['', ':1', ':443', ':8443', ':0', ':08443'])
            for (final end in ['', '/']) 'https://$host$port$end',
        'http://play.example',
        'https://',
        'https://user@play.example',
        ' https://play.example',
      ];
      for (final text in texts) {
        final origin = MultiplayerServiceConfiguration.parse(
          text,
        ).origin?.toString();
        expect(
          LaunchSessionRequest.isServiceOrigin(text),
          origin == text,
          reason: text,
        );
        if (origin != null) {
          expect(
            LaunchSessionRequest.isServiceOrigin(origin),
            isTrue,
            reason: text,
          );
        }
      }
    });

    test('an invalid origin contacts nothing', () async {
      final repo = repository(
        service: MultiplayerServiceConfiguration.parse('http://play.example'),
      );
      expect(repo.serviceStatus, MultiplayerServiceStatus.invalid);
      final http = CountingHttpOverrides();
      await HttpOverrides.runWithHttpOverrides(() async {
        final failed =
            await repo.answerJoin(
                  JoinSession.start(
                    JoinLinkTarget(SessionLink.invite('ABCDE12345')),
                    privacy: SessionPrivacy.relayOnly,
                  ),
                )
                as JoinFailed;
        expect(failed.code, JoinFailureCode.serviceUnavailable);
        expect(failed.detail, contains('not valid'));
        final filter = ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1);
        final browse = await repo.browseInternet(filter);
        expect(browse.unavailableMessage, failed.detail);
        expect(browse.problem, MultiplayerServiceProblem.notConfigured);
        final status = await repo.readServiceStatus();
        expect(status.isAvailable, isFalse);
        expect(status.problem, MultiplayerServiceProblem.notConfigured);
        expect(
          (await repo.fetchInternetServer(hostAServerId)).message,
          failed.detail,
        );
      }, http);
      expect(http.clientsCreated, 0);
    });

    test('a cancelled request to the service gives no event', () async {
      final event = await repository().resolveTarget(
        JoinLinkTarget(SessionLink.invite('ABCDE12345')),
        cancellation: CancellationToken()..cancel(),
      );
      expect(event, isNull);
    });
  });

  test('network diagnostics report the service and LAN state', () async {
    final repo = repository();
    await repo.startLanBrowsing();
    final report = await repo.checkNetwork();
    expect(report.service, MultiplayerServiceStatus.unconfigured);
    expect(report.lan.status, LanDiscoveryStatus.listening);
    expect(report.gamePortUdp.result, PortCheckResult.notChecked);
    expect(report.upnp, NetworkCheckState.notAvailable);
    expect(report.turn, NetworkCheckState.skipped);
    final checked = await repo.checkNetwork(checkPorts: true);
    expect(checked.gamePortUdp.result, PortCheckResult.available);
    expect(checked.lanPort.result, PortCheckResult.available);
  });
}
