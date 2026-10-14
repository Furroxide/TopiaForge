import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// The projection follows the launcher's multiplayer store, and changes the
/// player made in game flow back through the journal.
void main() {
  final settings = MultiplayerSettings(displayName: 'Sam');
  final hosting = MultiplayerHostingDefaults.forPlayer('Sam');
  MultiplayerPreferencesSource source({
    MultiplayerSettings? from,
    MultiplayerHostingDefaults? hostingDefaults,
    String? origin,
  }) => MultiplayerPreferencesValues(
    settings: from ?? settings,
    hostingDefaults: hostingDefaults ?? hosting,
    serviceOrigin: origin,
  );

  test('the first round writes revision 0', () {
    final sync = MultiplayerPreferencesSync.of(source: source());
    expect(sync.needsWrite, isTrue);
    expect(sync.changedInGame, isFalse);
    expect(sync.projection.revision, 0);
    expect(sync.projection.displayName, 'Sam');
    expect(sync.projection.hosting.name, "Sam's server");
  });

  test('nothing changed writes nothing', () {
    final first = MultiplayerPreferencesSync.of(source: source()).projection;
    final again = MultiplayerPreferencesSync.of(
      previous: first,
      journal: MultiplayerPreferencesJournal(const []),
      source: source(),
    );
    expect(again.needsWrite, isFalse);
    expect(again.projection.revision, 0);
  });

  test('a launcher change raises the revision', () {
    final first = MultiplayerPreferencesSync.of(source: source()).projection;
    final next = MultiplayerPreferencesSync.of(
      previous: first,
      source: source(
        from: settings.withDisplayName('Charlie'),
        origin: 'https://mp.topiaforge.dev',
      ),
    );
    expect(next.needsWrite, isTrue);
    expect(next.changedInGame, isFalse);
    expect(next.projection.revision, 1);
    expect(next.projection.displayName, 'Charlie');
    expect(next.projection.serviceOrigin, 'https://mp.topiaforge.dev');
  });

  test('changes made in game flow back to the launcher once', () {
    final first = MultiplayerPreferencesSync.of(source: source()).projection;
    final journal = MultiplayerPreferencesJournal([
      DisplayNameChange(sequence: 1, displayName: 'Zoë'),
      DisclosureChange(sequence: 2, disclosure: 1),
      VoiceChange(sequence: 3, consent: 1, enabled: true),
      HostingChange(
        sequence: 4,
        hosting: MultiplayerHostingDefaults(name: 'Zoë hosts', maxPlayers: 4),
      ),
    ]);
    final round = MultiplayerPreferencesSync.of(
      previous: first,
      journal: journal,
      source: source(),
    );
    expect(round.changedInGame, isTrue);
    expect(round.needsWrite, isTrue);
    expect(round.projection.revision, 1);
    expect(round.projection.journalApplied, 4);
    expect(round.settings.displayName, 'Zoë');
    expect(round.settings.needsPrivacyDisclosure, isFalse);
    expect(round.settings.voiceActive, isTrue);
    expect(round.hostingDefaults.maxPlayers, 4);

    // The launcher kept them; the same journal is old news now.
    final kept = MultiplayerPreferencesSync.of(
      previous: round.projection,
      journal: journal,
      source: source(
        from: round.settings,
        hostingDefaults: round.hostingDefaults,
      ),
    );
    expect(kept.changedInGame, isFalse);
    expect(kept.needsWrite, isFalse);
    expect(kept.projection.revision, 1);
  });

  test('the projection never stores direct play', () {
    final direct = settings.acknowledgePrivacyDisclosure();
    final sync = MultiplayerPreferencesSync.of(source: source(from: direct));
    expect(
      LaunchTransport.writePreferences(sync.projection),
      contains('"mode":"relay-only"'),
    );
  });
}
