import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  MultiplayerPreferences preferences([MultiplayerSettings? settings]) =>
      MultiplayerPreferences(settings: settings ?? MultiplayerSettings());

  group('multiplayer preferences', () {
    test('start from the plan: hosting for the player, no notice read', () {
      final fresh = preferences(MultiplayerSettings(displayName: 'Sam'));
      expect(fresh.hosting.toJson(), {
        'maxPlayers': 8,
        'name': "Sam's server",
        'port': LanBeacon.defaultGamePort,
        'visibility': 'lan',
        'voice': false,
      });
      expect(fresh.networkNotice, 0);
      expect(fresh.needsNetworkNotice, isTrue);
      expect(fresh.health, MultiplayerPreferencesHealth.sound);
      expect(fresh.readable, isTrue);
      expect(
        MultiplayerPreferences(
          settings: MultiplayerSettings(),
          health: MultiplayerPreferencesHealth.unreadable,
        ).readable,
        isFalse,
      );
      expect(
        () => MultiplayerPreferences(
          settings: MultiplayerSettings(),
          networkNotice: -1,
        ),
        throwsArgumentError,
      );
    });

    test('a read notice is not asked for again', () {
      final read = MultiplayerPreferences(
        settings: MultiplayerSettings(),
        networkNotice: MultiplayerPreferences.currentNetworkNotice,
      );
      expect(read.needsNetworkNotice, isFalse);
    });
  });

  group('a change from settings', () {
    test('changes only what it names', () {
      final before = MultiplayerPreferences(
        settings: MultiplayerSettings(
          displayName: 'Sam',
          linkHandlingEnabled: false,
        ),
        networkNotice: 1,
        health: MultiplayerPreferencesHealth.salvaged,
      );
      final after = const MultiplayerPreferencesChange(
        displayName: 'Robin',
      ).applyTo(before);
      expect(after.settings.displayName, 'Robin');
      expect(after.settings.linkHandlingEnabled, isFalse);
      expect(after.settings.lanDiscoveryEnabled, isTrue);
      expect(after.hosting.toJson(), before.hosting.toJson());
      expect(after.networkNotice, 1);
      expect(after.health, MultiplayerPreferencesHealth.salvaged);
      final same = const MultiplayerPreferencesChange().applyTo(before);
      expect(same.settings.toJson(), before.settings.toJson());
    });

    test('sets each preference the settings page shows', () {
      final before = MultiplayerPreferences(
        settings: MultiplayerSettings(displayName: 'Sam'),
        hosting: MultiplayerHostingDefaults.forPlayer('Sam'),
      );
      final after = const MultiplayerPreferencesChange(
        acknowledgeDisclosure: true,
        hostingPort: 0,
        upnp: true,
        lanDiscovery: false,
        acknowledgeNetworkNotice: true,
      ).applyTo(before);
      expect(after.settings.needsPrivacyDisclosure, isFalse);
      expect(
        after.settings.acknowledgedPrivacyDisclosure,
        MultiplayerSettings.currentPrivacyDisclosure,
      );
      expect(after.hosting.port, 0);
      expect(after.hosting.name, "Sam's server");
      expect(after.settings.upnpEnabled, isTrue);
      expect(after.settings.lanDiscoveryEnabled, isFalse);
      expect(after.networkNotice, MultiplayerPreferences.currentNetworkNotice);
      expect(after.needsNetworkNotice, isFalse);
    });

    test('voice turns on only with consent, and withdrawing turns it off', () {
      final fresh = preferences();
      expect(
        () => const MultiplayerPreferencesChange(voice: true).applyTo(fresh),
        throwsArgumentError,
      );
      final consented = const MultiplayerPreferencesChange(
        voiceConsent: true,
      ).applyTo(fresh);
      expect(consented.settings.hasVoiceConsent, isTrue);
      expect(consented.settings.voiceActive, isTrue);
      final muted = const MultiplayerPreferencesChange(
        voice: false,
      ).applyTo(consented);
      expect(muted.settings.hasVoiceConsent, isTrue);
      expect(muted.settings.voiceEnabled, isFalse);
      final withdrawn = const MultiplayerPreferencesChange(
        voiceConsent: false,
      ).applyTo(consented);
      expect(withdrawn.settings.hasVoiceConsent, isFalse);
      expect(withdrawn.settings.voiceEnabled, isFalse);
      expect(withdrawn.settings.voiceConsent, 0);
    });

    test('refuses a name outside the rule and a port outside 0 to 65535', () {
      final fresh = MultiplayerPreferences(
        settings: MultiplayerSettings(),
        hosting: MultiplayerHostingDefaults.forPlayer('Player'),
      );
      for (final name in ['A', 'x' * 25, ' Sam', 'Sam￿']) {
        expect(
          () => MultiplayerPreferencesChange(displayName: name).applyTo(fresh),
          throwsArgumentError,
          reason: name,
        );
      }
      for (final port in [-1, 65536]) {
        expect(
          () => MultiplayerPreferencesChange(hostingPort: port).applyTo(fresh),
          throwsFormatException,
          reason: '$port',
        );
      }
      expect(
        const MultiplayerPreferencesChange(
          hostingPort: 65535,
        ).applyTo(fresh).hosting.port,
        65535,
      );
    });

    test('never lowers the notice the player read', () {
      final newer = MultiplayerPreferences(
        settings: MultiplayerSettings(),
        networkNotice: MultiplayerPreferences.currentNetworkNotice + 1,
      );
      expect(
        const MultiplayerPreferencesChange(
          acknowledgeNetworkNotice: true,
        ).applyTo(newer).networkNotice,
        MultiplayerPreferences.currentNetworkNotice + 1,
      );
    });

    test('names what the game can change too', () {
      const shared = [
        MultiplayerPreferencesChange(displayName: 'Sam'),
        MultiplayerPreferencesChange(acknowledgeDisclosure: true),
        MultiplayerPreferencesChange(hostingPort: 27250),
        MultiplayerPreferencesChange(upnp: false),
        MultiplayerPreferencesChange(lanDiscovery: true),
        MultiplayerPreferencesChange(voiceConsent: false),
        MultiplayerPreferencesChange(voice: false),
      ];
      for (final change in shared) {
        expect(change.sharedWithGame, isTrue);
      }
      expect(
        const MultiplayerPreferencesChange(
          acknowledgeNetworkNotice: true,
        ).sharedWithGame,
        isFalse,
      );
      expect(const MultiplayerPreferencesChange().sharedWithGame, isFalse);
    });
  });

  group('a round with the game', () {
    test('says what it did, with the preferences after it', () {
      final kept = preferences();
      final rounds = <MultiplayerPreferencesRound>[
        MultiplayerPreferencesSynced(kept, changedInGame: true),
        MultiplayerPreferencesRoundSkipped(kept),
        MultiplayerPreferencesRoundFailed(
          kept,
          MultiplayerRoundProblem.gameCopyNotWritten,
        ),
      ];
      for (final round in rounds) {
        expect(round.preferences, same(kept));
      }
      expect(
        rounds.map(
          (round) => switch (round) {
            MultiplayerPreferencesSynced(:final changedInGame) =>
              'synced $changedInGame',
            MultiplayerPreferencesRoundSkipped() => 'skipped',
            MultiplayerPreferencesRoundFailed(:final problem) => problem.name,
          },
        ),
        ['synced true', 'skipped', 'gameCopyNotWritten'],
      );
    });
  });

  test('a refused change says so plainly', () {
    const refused = MultiplayerPreferencesException(
      'Your multiplayer settings could not be read just now.',
      unreadable: true,
    );
    expect(refused.unreadable, isTrue);
    expect(
      '$refused',
      'MultiplayerPreferencesException: Your multiplayer settings could not '
          'be read just now.',
    );
  });
}
