import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';
import 'multiplayer_unreadable_file.dart';

/// A stored file that another program holds unreadable is never written
/// over, and nothing takes it for empty or for the defaults. On Windows the
/// file is held with delete sharing only, so a write could still replace it;
/// elsewhere its mode denies reading, and a write could replace it too.
void main() {
  final now = DateTime.utc(2026, 10, 15, 12);
  late Directory root;
  late String dataRoot;
  late String folder;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-mp-held-');
    dataRoot = root.resolveSymbolicLinksSync();
    folder = p.join(dataRoot, LocalMultiplayerRepository.folderName);
    Directory(folder).createSync(recursive: true);
  });
  tearDown(() => root.deleteSync(recursive: true));

  File settingsFile() =>
      File(p.join(folder, MultiplayerStore.settingsFileName));
  File knownHostsFile() =>
      File(p.join(folder, MultiplayerStore.knownHostsFileName));
  MultiplayerStore store() => MultiplayerStore(folder, clock: () => now);

  /// What the player chose: a name, the disclosure seen, discovery off and
  /// one favorite.
  final chosen = MultiplayerSettings(
    displayName: 'Charl',
    acknowledgedPrivacyDisclosure: MultiplayerSettings.currentPrivacyDisclosure,
    lanDiscoveryEnabled: false,
    favorites: ServerFavorites([
      ServerBookmark(
        serverId: hostAServerId,
        name: 'Old Favorite',
        savedAt: DateTime.utc(2026, 10, 1),
      ),
    ]),
  );

  Future<List<int>> storeChosen() async {
    await store().saveSettings(chosen);
    return settingsFile().readAsBytesSync();
  }

  Matcher unreadable() => throwsA(
    isA<MultiplayerStoreException>()
        .having((error) => error.unreadable, 'unreadable', isTrue)
        .having(
          (error) => error.message,
          'message',
          allOf(isNot(contains(dataRoot)), contains('could not be read')),
        ),
  );

  LocalMultiplayerRepository repository({LanDiscoveryClient? lan}) {
    final created = LocalMultiplayerRepository(
      dataRoot: dataRoot,
      service: MultiplayerServiceConfiguration.none,
      lan: lan ?? LanRig.fresh().client,
      lookup: (host) async => throw StateError('No lookups expected.'),
      diagnostics: MultiplayerNetworkDiagnostics(
        bindAddress: InternetAddress.loopbackIPv4,
      ),
      clock: () => now,
    );
    addTearDown(created.dispose);
    return created;
  }

  group('the store', () {
    test('stands in everything off for settings it cannot read', () async {
      final original = await storeChosen();
      final release = await holdUnreadable(settingsFile());
      if (release == null) return;
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.unreadable);
      final settings = load.settings;
      expect(settings.toJson(), MultiplayerStore.unreadableSettings.toJson());
      expect(settings.lanDiscoveryEnabled, isFalse);
      expect(settings.upnpEnabled, isFalse);
      expect(settings.voiceActive, isFalse);
      expect(settings.linkHandlingEnabled, isFalse);
      // Hide my IP stays on: a direct path still needs the disclosure.
      expect(settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
      expect(settings.needsPrivacyDisclosure, isTrue);
      expect(
        () => settings.privacyForSession(allowDirect: true),
        throwsStateError,
      );
      await release();
      expect(settingsFile().readAsBytesSync(), original);
      expect(Directory(folder).listSync(), hasLength(1));
    });

    test('never writes over settings it cannot read', () async {
      final original = await storeChosen();
      final shared = store();
      final release = await holdUnreadable(settingsFile());
      if (release == null) return;
      var changes = 0;
      await expectLater(
        shared.updateSettings((settings) {
          changes++;
          return settings.withDisplayName('Overwritten');
        }),
        unreadable(),
      );
      await expectLater(
        shared.saveSettings(MultiplayerSettings()),
        unreadable(),
      );
      expect(changes, 0);
      await release();
      expect(settingsFile().readAsBytesSync(), original);
      expect((await shared.loadSettings()).settings.toJson(), chosen.toJson());
      final renamed = await shared.updateSettings(
        (settings) => settings.withDisplayName('Charlotte'),
      );
      expect(renamed.favorites.entries.single.name, 'Old Favorite');
      expect(renamed.lanDiscoveryEnabled, isFalse);
    });

    test(
      'never takes known hosts it cannot read for an empty record',
      () async {
        final typed = JoinAddressTarget(host: '127.0.0.1', port: 27250);
        await store().updateKnownHosts(
          (hosts) => hosts.remember(typed, hostAServerId, now),
        );
        final original = knownHostsFile().readAsBytesSync();
        final release = await holdUnreadable(knownHostsFile());
        if (release == null) return;
        var changes = 0;
        await expectLater(store().loadKnownHosts(), unreadable());
        await expectLater(
          store().updateKnownHosts((hosts) {
            changes++;
            return hosts.remember(
              JoinAddressTarget(host: '127.0.0.1', port: 27300),
              hostBServerId,
              now,
            );
          }),
          unreadable(),
        );
        expect(changes, 0);
        await release();
        expect(knownHostsFile().readAsBytesSync(), original);
        expect(
          (await store().loadKnownHosts()).check(typed, hostBServerId),
          KnownHostCheck.mismatch,
        );
      },
    );
  });

  group('the repository', () {
    test('opens no socket while the settings cannot be read', () async {
      await storeChosen();
      final rig = LanRig.fresh();
      final repo = repository(lan: rig.client);
      final release = await holdUnreadable(settingsFile());
      if (release == null) return;
      final snapshot = await repo.startLanBrowsing();
      expect(rig.binder.binds, isEmpty);
      expect(snapshot.status, LanDiscoveryStatus.unavailable);
      expect(snapshot.problem, LanDiscoveryProblem.settingsUnreadable);
      expect(snapshot.message, contains('could not be read'));
      await release();
      // Readable again, the player's choice holds.
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.disabled,
      );
      expect(rig.binder.binds, isEmpty);
    });

    test('a favorite or join recorded meanwhile loses nothing', () async {
      final original = await storeChosen();
      final repo = repository();
      final release = await holdUnreadable(settingsFile());
      if (release == null) return;
      await expectLater(
        repo.addFavorite(serverId: hostBServerId, name: 'New Favorite'),
        unreadable(),
      );
      await expectLater(
        repo.recordJoin(serverId: hostBServerId, name: 'New Favorite'),
        unreadable(),
      );
      await release();
      expect(settingsFile().readAsBytesSync(), original);
      final settings = (await repo.loadSettings()).settings;
      expect(settings.displayName, 'Charl');
      expect(settings.needsPrivacyDisclosure, isFalse);
      expect(settings.lanDiscoveryEnabled, isFalse);
      expect(settings.favorites.entries.single.serverId, hostAServerId);
    });

    test(
      'turning discovery off stops browsing even when the save fails',
      () async {
        await store().saveSettings(MultiplayerSettings());
        final rig = LanRig.fresh();
        final repo = repository(lan: rig.client);
        expect(
          (await repo.startLanBrowsing()).status,
          LanDiscoveryStatus.listening,
        );
        final release = await holdUnreadable(settingsFile());
        if (release == null) return;
        await expectLater(
          repo.saveSettings(MultiplayerSettings().withLanDiscovery(false)),
          unreadable(),
        );
        expect(rig.client.isListening, isFalse);
        expect(repo.lanSnapshot.status, LanDiscoveryStatus.disabled);
      },
    );
  });

  group('trust on first use', () {
    JoinSessionState start(JoinTarget target, {String? trusted}) =>
        JoinSession.start(
          target,
          privacy: SessionPrivacy.relayOnly,
          trustedServerId: trusted,
        );

    Future<LoopbackDescriptorHost> serving(List<int> body) async {
      final host = await LoopbackDescriptorHost.serving(body);
      addTearDown(host.close);
      return host;
    }

    test('fails closed while the record cannot be read', () async {
      final hostA = await serving(hostADescriptor);
      final hostB = await serving(hostBDescriptor);
      final repo = repository();
      final atA = JoinAddressTarget(host: '127.0.0.1', port: hostA.port);
      final atB = JoinAddressTarget(host: '127.0.0.1', port: hostB.port);
      expect(await repo.answerJoin(start(atA)), isA<JoinDescriptorVerified>());
      final original = knownHostsFile().readAsBytesSync();
      final release = await holdUnreadable(knownHostsFile());
      if (release == null) return;

      // B's address is new, but a record that was not read cannot say so.
      final state = start(atB);
      final event = await repo.answerJoin(state) as JoinFailed;
      expect(event.code, JoinFailureCode.identityChanged);
      expect(event.servedServerId, hostBServerId);
      expect(event.detail, contains('could not be read'));
      expect(
        JoinSession.reduce(state, event).outcome!.failure!.code,
        JoinFailureCode.identityChanged,
      );
      // Trusting B needs no record.
      expect(
        await repo.answerJoin(start(atB, trusted: hostBServerId)),
        isA<JoinDescriptorVerified>(),
      );

      await release();
      expect(knownHostsFile().readAsBytesSync(), original);
      // A's record survived, so another server at A's address is a change.
      hostA.respond = (socket, _) =>
          LoopbackDescriptorHost.answer(socket, hostBDescriptor);
      final changed = await repo.answerJoin(start(atA)) as JoinFailed;
      expect(changed.code, JoinFailureCode.identityChanged);
      expect(changed.detail, contains('served a different server before'));
    });
  });
}
