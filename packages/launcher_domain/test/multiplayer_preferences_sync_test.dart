import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// The game's journal of in-game changes.
const _epoch = '5f0c2a9e8d7b6a5f4e3d2c1b0a998877';

/// A journal the launcher took in before staging was lost.
const _before = '0d1c2b3a49586776a5b4c3d2e1f00f1e';

/// The projection follows the launcher's multiplayer store, and changes the
/// player made in game flow back through the journal. The store keeps the
/// projection's revision and its position in the journal, by epoch, with the
/// settings, so a lost projection never re-applies an old change or lowers
/// the revision, and a journal that started over is applied from its first
/// change.
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
    expect(sync.journalEpoch, isNull);
  });

  test('nothing changed writes nothing', () {
    final first = MultiplayerPreferencesSync.of(source: source()).projection;
    final again = MultiplayerPreferencesSync.of(
      previous: first,
      journal: MultiplayerPreferencesJournal(_epoch, const []),
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
    final journal = MultiplayerPreferencesJournal(_epoch, [
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
    expect(round.revision, 1);
    expect((round.journalEpoch, round.journalApplied), (_epoch, 4));
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

  test('a lost projection is rewritten from the store', () {
    final store = _Store(settings);
    var projection = store.round(null, null);
    expect(projection.revision, 0);
    final journal = MultiplayerPreferencesJournal(_epoch, [
      DisplayNameChange(sequence: 1, displayName: 'InGame'),
    ]);
    projection = store.round(projection, journal);
    expect(store.settings.displayName, 'InGame');
    expect(projection.revision, 1);

    store.settings = store.settings.withDisplayName('Launcher');
    projection = store.round(projection, journal);
    expect(projection.revision, 2);

    // The projection is deleted or damaged; the journal still holds change 1.
    projection = store.round(null, journal);
    expect(store.settings.displayName, 'Launcher');
    expect(projection.displayName, 'Launcher');
    expect(projection.revision, 3);
    expect((projection.journalEpoch, projection.journalApplied), (_epoch, 1));
  });

  test('a journal started over after staging was lost is applied', () {
    // The store took in journal _before up to change 6. Staging then lost the
    // projection and the journal, and a game started without the launcher
    // journaled the player's rename as change 1 of a new journal.
    final store = _Store(settings.withDisplayName('Launcher'))
      ..revision = 4
      ..journalEpoch = _before
      ..journalApplied = 6;
    final projection = store.round(
      null,
      MultiplayerPreferencesJournal(_epoch, [
        DisplayNameChange(sequence: 1, displayName: 'InGame'),
      ]),
    );
    expect(store.settings.displayName, 'InGame');
    expect(projection.displayName, 'InGame');
    expect(projection.revision, 5);
    expect((store.journalEpoch, store.journalApplied), (_epoch, 1));
    expect((projection.journalEpoch, projection.journalApplied), (_epoch, 1));
  });

  test('a journal taken in already applies nothing twice', () {
    final journal = MultiplayerPreferencesJournal(_epoch, [
      DisplayNameChange(sequence: 1, displayName: 'InGame'),
      VoiceChange(sequence: 2, consent: 1, enabled: true),
    ]);
    final store = _Store(settings);
    var projection = store.round(null, journal);
    expect(store.settings.displayName, 'InGame');
    expect(store.settings.voiceActive, isTrue);
    store.settings = store.settings
        .withDisplayName('Launcher')
        .withVoice(false);
    projection = store.round(projection, journal);
    expect(projection.displayName, 'Launcher');

    // The projection is lost: the store's record counts the journal.
    expect(store.round(null, journal).displayName, 'Launcher');
    expect(store.settings.voiceActive, isFalse);
    // The store lost its record: the projection's counts the journal.
    final forgetful = _Store(store.settings);
    final again = forgetful.round(projection, journal);
    expect(again.displayName, 'Launcher');
    expect(forgetful.settings.voiceActive, isFalse);
  });

  test('a projection behind the store applies nothing twice', () {
    final store = _Store(settings);
    final behind = store.round(
      null,
      MultiplayerPreferencesJournal(_epoch, [
        DisplayNameChange(sequence: 1, displayName: 'InGame'),
      ]),
    );
    // The game drops the change the projection counts and journals change 2.
    // The store takes it in, but the projection write fails, so the
    // projection on disk still counts change 1 of the same journal.
    final journal = MultiplayerPreferencesJournal(_epoch, [
      VoiceChange(sequence: 2, consent: 1, enabled: true),
    ]);
    store.round(behind, journal);
    expect((behind.journalEpoch, behind.journalApplied), (_epoch, 1));
    expect((store.journalEpoch, store.journalApplied), (_epoch, 2));
    store.settings = store.settings.withVoice(false);
    final projection = store.round(behind, journal);
    expect(store.settings.voiceActive, isFalse);
    expect(projection.voiceEnabled, isFalse);
    expect((projection.journalEpoch, projection.journalApplied), (_epoch, 2));
  });

  test('journals and journal positions name their epoch exactly', () {
    for (final epoch in [
      _epoch.toUpperCase(),
      _epoch.substring(1),
      '${_epoch}0',
      'g' * 32,
      '',
    ]) {
      expect(
        () => MultiplayerPreferencesJournal(epoch, const []),
        throwsFormatException,
        reason: epoch,
      );
      expect(
        () => MultiplayerPreferencesProjection.fromSettings(
          settings,
          hosting: hosting,
          revision: 0,
          journalApplied: 1,
          journalEpoch: epoch,
        ),
        throwsFormatException,
        reason: epoch,
      );
    }
    expect(
      () => MultiplayerPreferencesProjection.fromSettings(
        settings,
        hosting: hosting,
        revision: 0,
        journalApplied: 1,
      ),
      throwsFormatException,
      reason: 'A position names the journal it counts.',
    );
    expect(
      () => LaunchTransport.readPreferencesJournal(
        '{"changes":[],"schemaVersion":1}',
      ),
      throwsFormatException,
      reason: 'A journal names its epoch.',
    );
  });

  test('the revision never goes down', () {
    // The store saved revision 5, but the projection write failed at 3.
    final behind = MultiplayerPreferencesProjection.fromSettings(
      settings,
      hosting: hosting,
      revision: 3,
    );
    final ahead = MultiplayerPreferencesSync.of(
      previous: behind,
      source: MultiplayerPreferencesValues(
        settings: settings,
        hostingDefaults: hosting,
        revision: 5,
      ),
    );
    expect(ahead.needsWrite, isTrue);
    expect(ahead.revision, 6);

    // The store lost its record, but the projection still reads.
    final kept = MultiplayerPreferencesSync.of(
      previous: ahead.projection,
      source: MultiplayerPreferencesValues(
        settings: settings,
        hostingDefaults: hosting,
      ),
    );
    expect(kept.needsWrite, isFalse);
    expect(kept.revision, 6);
  });

  test('without a hosting store, an in-game hosting change is kept', () {
    final store = _Store(settings);
    final journal = MultiplayerPreferencesJournal(_epoch, [
      HostingChange(
        sequence: 1,
        hosting: MultiplayerHostingDefaults(name: 'Late night', maxPlayers: 3),
      ),
    ]);
    var projection = store.round(null, null);
    expect(projection.hosting.name, "Sam's server");
    projection = store.round(projection, journal);
    expect(projection.hosting.name, 'Late night');
    projection = store.round(projection, journal);
    projection = store.round(projection, null);
    expect(projection.hosting.name, 'Late night');
    expect(projection.hosting.maxPlayers, 3);
  });

  test('the projection never stores direct play', () {
    final direct = settings.acknowledgePrivacyDisclosure();
    final sync = MultiplayerPreferencesSync.of(source: source(from: direct));
    expect(
      LaunchTransport.writePreferences(sync.projection),
      contains('"mode":"relay-only"'),
    );
  });

  // The game drops every change a later one supersedes, so a long session
  // never fills the journal (`MultiplayerPreferencesJournal.Append` in C#).
  // That holds only while each change sets its whole preference and an
  // acknowledgement never lowers the disclosure.
  test('a superseded change never alters what a journal applies', () {
    final changes = <MultiplayerPreferenceChange>[
      DisplayNameChange(sequence: 1, displayName: 'Alex'),
      VoiceChange(sequence: 2, consent: 1, enabled: true),
      DisclosureChange(sequence: 3, disclosure: 2),
      NetworkChange(sequence: 4, lanDiscovery: false, upnp: true),
      DisplayNameChange(sequence: 5, displayName: 'Robin'),
      DisclosureChange(sequence: 6, disclosure: 1),
      HostingChange(
        sequence: 7,
        hosting: MultiplayerHostingDefaults(name: 'Late night', maxPlayers: 3),
      ),
      VoiceChange(sequence: 8, consent: 1, enabled: false),
      NetworkChange(sequence: 9, lanDiscovery: true, upnp: false),
      HostingChange(
        sequence: 10,
        hosting: MultiplayerHostingDefaults(name: 'Early bird'),
      ),
    ];
    bool supersedes(
      MultiplayerPreferenceChange later,
      MultiplayerPreferenceChange earlier,
    ) =>
        later.runtimeType == earlier.runtimeType &&
        (later is! DisclosureChange ||
            later.disclosure >= (earlier as DisclosureChange).disclosure);
    final kept = [
      for (final (index, change) in changes.indexed)
        if (!changes.skip(index + 1).any((later) => supersedes(later, change)))
          change,
    ];
    expect(kept.map((change) => change.sequence), [3, 5, 6, 8, 9, 10]);
    for (var applied = 0; applied <= changes.length; applied++) {
      final projection = MultiplayerPreferencesProjection.fromSettings(
        settings,
        hosting: hosting,
        revision: 3,
        journalApplied: applied,
        journalEpoch: _epoch,
      );
      expect(
        LaunchTransport.writePreferences(
          MultiplayerPreferencesJournal(_epoch, kept).applyTo(projection),
        ),
        LaunchTransport.writePreferences(
          MultiplayerPreferencesJournal(_epoch, changes).applyTo(projection),
        ),
        reason: 'with changes through $applied applied',
      );
    }
  });
}

/// A launcher store with no hosting defaults yet, as the multiplayer store is
/// today: it keeps the settings, the revision and the journal position, by
/// epoch.
final class _Store {
  _Store(this.settings);
  MultiplayerSettings settings;
  int? revision;
  String? journalEpoch;
  int journalApplied = 0;

  /// One round against the projection last read, saving before it returns
  /// what would be written.
  MultiplayerPreferencesProjection round(
    MultiplayerPreferencesProjection? previous,
    MultiplayerPreferencesJournal? journal,
  ) {
    final sync = MultiplayerPreferencesSync.of(
      previous: previous,
      journal: journal,
      source: MultiplayerPreferencesValues(
        settings: settings,
        revision: revision,
        journalEpoch: journalEpoch,
        journalApplied: journalApplied,
      ),
    );
    if (sync.needsWrite) {
      settings = sync.settings;
      revision = sync.revision;
      journalEpoch = sync.journalEpoch;
      journalApplied = sync.journalApplied;
    }
    return sync.projection;
  }
}
