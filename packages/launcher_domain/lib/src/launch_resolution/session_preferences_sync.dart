part of '../launch_resolution.dart';

/// Where the launcher's multiplayer preferences live: the multiplayer
/// store's settings and its record of the projection, and the configured
/// control-plane origin. The store implements it, or a small adapter does, so
/// the projection never becomes a second copy of the player's settings.
abstract interface class MultiplayerPreferencesSource {
  MultiplayerSettings get settings;

  /// The hosting defaults the store holds, or null while no store holds
  /// them: the projection then keeps its own, the game's changes included,
  /// and starts from [MultiplayerHostingDefaults.forPlayer].
  MultiplayerHostingDefaults? get hostingDefaults;

  /// The control plane's origin, or null when none is configured.
  String? get serviceOrigin;

  /// The projection revision the store last saved, or null before the first.
  int? get revision;

  /// The last journal sequence the store has taken in.
  int get journalApplied;
}

/// Plain [MultiplayerPreferencesSource] values.
final class MultiplayerPreferencesValues
    implements MultiplayerPreferencesSource {
  const MultiplayerPreferencesValues({
    required this.settings,
    this.hostingDefaults,
    this.serviceOrigin,
    this.revision,
    this.journalApplied = 0,
  });

  @override
  final MultiplayerSettings settings;
  @override
  final MultiplayerHostingDefaults? hostingDefaults;
  @override
  final String? serviceOrigin;
  @override
  final int? revision;
  @override
  final int journalApplied;
}

/// One round of keeping the game's projection and the launcher's
/// preferences in step.
///
/// The launcher's store is the record: it keeps the projection's [revision]
/// and [journalApplied] with the player's settings, and a round that writes
/// saves the store before the projection. A projection that is missing or
/// damaged is then rewritten from the store, a journal change is never
/// applied twice, and the revision never goes down.
final class MultiplayerPreferencesSync {
  const MultiplayerPreferencesSync._({
    required this.projection,
    required this.needsWrite,
    required this.changedInGame,
    required this.settings,
    required this.hostingDefaults,
  });

  /// The round after [previous], the projection last written if it reads,
  /// given the game's [journal] and the launcher's current [source]:
  ///
  /// - the game's changes after the later of `source.journalApplied` and
  ///   `previous.journalApplied` apply on top of the launcher's current
  ///   values, since they are the player's latest choices, made in game;
  /// - every write takes the next revision after the later of
  ///   `source.revision` and `previous.revision`, starting from 0, and a
  ///   round that changes nothing writes nothing.
  factory MultiplayerPreferencesSync.of({
    MultiplayerPreferencesProjection? previous,
    MultiplayerPreferencesJournal? journal,
    required MultiplayerPreferencesSource source,
  }) {
    int? later(int? left, int? right) =>
        left == null || (right != null && right > left) ? right : left;
    final known = later(source.revision, previous?.revision);
    final applied = later(source.journalApplied, previous?.journalApplied)!;
    final current = MultiplayerPreferencesProjection.fromSettings(
      source.settings,
      hosting:
          source.hostingDefaults ??
          previous?.hosting ??
          MultiplayerHostingDefaults.forPlayer(source.settings.displayName),
      revision: known ?? 0,
      journalApplied: applied,
      serviceOrigin: source.serviceOrigin,
    );
    final merged = journal?.applyTo(current) ?? current;
    final changedInGame = !identical(merged, current);
    final needsWrite =
        previous == null ||
        changedInGame ||
        previous.revision != known ||
        !_sameValues(previous, merged);
    final projection = needsWrite
        ? merged._with(
            revision: known == null ? 0 : known + 1,
            journalApplied: merged.journalApplied,
          )
        : previous;
    return MultiplayerPreferencesSync._(
      projection: projection,
      needsWrite: needsWrite,
      changedInGame: changedInGame,
      settings: projection.applyTo(source.settings),
      hostingDefaults: projection.hosting,
    );
  }

  /// The projection the game should read.
  final MultiplayerPreferencesProjection projection;

  /// Whether [projection] differs from the one last written. The caller then
  /// saves its store first ([settings], [hostingDefaults], [revision] and
  /// [journalApplied]) and writes [projection] after.
  final bool needsWrite;

  /// Whether the game's journal changed a preference.
  final bool changedInGame;

  /// The launcher's settings with the game's changes.
  final MultiplayerSettings settings;
  final MultiplayerHostingDefaults hostingDefaults;

  /// The revision the store keeps with the settings.
  int get revision => projection.revision;

  /// The journal position the store keeps with the settings.
  int get journalApplied => projection.journalApplied;

  static bool _sameValues(
    MultiplayerPreferencesProjection left,
    MultiplayerPreferencesProjection right,
  ) {
    Map<String, Object?> values(MultiplayerPreferencesProjection value) =>
        Map.of(value.toJson())
          ..remove('revision')
          ..remove('journalApplied');
    return jsonEncode(values(left)) == jsonEncode(values(right)) &&
        left.journalApplied == right.journalApplied;
  }
}
