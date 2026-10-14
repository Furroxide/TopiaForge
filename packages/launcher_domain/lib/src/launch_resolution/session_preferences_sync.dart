part of '../launch_resolution.dart';

/// Where the launcher's multiplayer preferences live: the multiplayer
/// store's settings and hosting defaults, and the configured control-plane
/// origin. The store implements it, or a small adapter does, so the
/// projection never becomes a second copy of the player's settings.
abstract interface class MultiplayerPreferencesSource {
  MultiplayerSettings get settings;
  MultiplayerHostingDefaults get hostingDefaults;

  /// The control plane's origin, or null when none is configured.
  String? get serviceOrigin;
}

/// Plain [MultiplayerPreferencesSource] values.
final class MultiplayerPreferencesValues
    implements MultiplayerPreferencesSource {
  const MultiplayerPreferencesValues({
    required this.settings,
    required this.hostingDefaults,
    this.serviceOrigin,
  });

  @override
  final MultiplayerSettings settings;
  @override
  final MultiplayerHostingDefaults hostingDefaults;
  @override
  final String? serviceOrigin;
}

/// One round of keeping the game's projection and the launcher's
/// preferences in step.
final class MultiplayerPreferencesSync {
  const MultiplayerPreferencesSync._({
    required this.projection,
    required this.needsWrite,
    required this.changedInGame,
    required this.settings,
    required this.hostingDefaults,
  });

  /// The round after [previous], the projection last written if any, given
  /// the game's [journal] and the launcher's current [source]:
  ///
  /// - the game's changes after `previous.journalApplied` apply on top of
  ///   the launcher's current values, since they are the player's latest
  ///   choices, made in game;
  /// - the revision rises by one whenever what the game would read changes,
  ///   and otherwise [previous] stays as it is.
  factory MultiplayerPreferencesSync.of({
    MultiplayerPreferencesProjection? previous,
    MultiplayerPreferencesJournal? journal,
    required MultiplayerPreferencesSource source,
  }) {
    final revision = previous?.revision ?? 0;
    final applied = previous?.journalApplied ?? 0;
    final current = MultiplayerPreferencesProjection.fromSettings(
      source.settings,
      hosting: source.hostingDefaults,
      revision: revision,
      journalApplied: applied,
      serviceOrigin: source.serviceOrigin,
    );
    final merged = journal?.applyTo(current) ?? current;
    final changedInGame = !identical(merged, current);
    var next = merged;
    if (!changedInGame && previous != null && !_sameValues(previous, next)) {
      next = current._with(revision: revision + 1, journalApplied: applied);
    }
    return MultiplayerPreferencesSync._(
      projection: next,
      needsWrite:
          previous == null ||
          previous.revision != next.revision ||
          !_sameValues(previous, next),
      changedInGame: changedInGame,
      settings: next.applyTo(source.settings),
      hostingDefaults: next.hosting,
    );
  }

  /// The projection the game should read.
  final MultiplayerPreferencesProjection projection;

  /// Whether [projection] differs from the one last written.
  final bool needsWrite;

  /// Whether the game's journal changed a preference. The caller then keeps
  /// [settings] and [hostingDefaults] in its store.
  final bool changedInGame;

  /// The launcher's settings with the game's changes.
  final MultiplayerSettings settings;
  final MultiplayerHostingDefaults hostingDefaults;

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
