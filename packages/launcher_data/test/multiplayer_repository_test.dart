import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

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
      await repo.saveSettings(MultiplayerSettings(displayName: 'Charl'));
      final file = File(p.join(root.path, 'multiplayer', 'settings.json'));
      expect(jsonDecode(file.readAsStringSync())['displayName'], 'Charl');
      expect((await repo.loadSettings()).settings.displayName, 'Charl');
    });

    test('favorites and history name servers by id and name only', () async {
      final repo = repository();
      await repo.addFavorite(serverId: hostAServerId, name: "Charl's Sandbox");
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
      await repo.saveSettings(off);
      expect(lan.isListening, isFalse);
      expect(repo.lanSnapshot.status, LanDiscoveryStatus.disabled);
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
      ]) {
        final configuration = MultiplayerServiceConfiguration.parse(text);
        expect(configuration.status, MultiplayerServiceStatus.configured);
        expect(configuration.isConfigured, isTrue);
        expect(configuration.origin.toString(), origin, reason: text);
        // The spelling the launch request accepts: `https://`, a lowercase
        // DNS name, a port without a leading zero, and nothing after.
        final host = configuration.origin!.host;
        expect(NetworkHost.tryNormalize(host), host);
        expect(NetworkHost.pathKindOf(host), isNull);
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

    test('an invalid or configured origin still contacts nothing in this '
        'build', () async {
      for (final (text, status, phrase) in [
        ('http://play.example', MultiplayerServiceStatus.invalid, 'not valid'),
        (
          'https://play.example',
          MultiplayerServiceStatus.configured,
          'not available in this version',
        ),
      ]) {
        final repo = repository(
          service: MultiplayerServiceConfiguration.parse(text),
        );
        expect(repo.serviceStatus, status);
        final event =
            await repo.answerJoin(
                  JoinSession.start(
                    JoinLinkTarget(SessionLink.invite('ABCDE12345')),
                    privacy: SessionPrivacy.relayOnly,
                  ),
                )
                as JoinFailed;
        expect(event.code, JoinFailureCode.serviceUnavailable);
        expect(event.detail, contains(phrase));
        expect((await repo.browseInternet()).unavailableMessage, event.detail);
      }
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
