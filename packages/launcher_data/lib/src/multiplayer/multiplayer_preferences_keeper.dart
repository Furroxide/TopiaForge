/// The player's multiplayer preferences: kept in the launcher's store, and
/// kept in step with the copy Robotopia reads.
library;

import 'package:launcher_domain/launcher_domain.dart';

import '../launch_running_probe.dart';
import '../launch_staging_store.dart';
import 'multiplayer_store.dart';

/// Whether a process runs from [executablePath]: true or false, or null when
/// it cannot be told. [probeLaunchProcessRunning] is the real one.
typedef GameProcessProbe = Future<bool?> Function(String executablePath);

/// The game's staging folder under [gameRoot], where the preferences
/// projection and the game's journal live.
typedef GameStagingOpener = LaunchStagingStore Function(String gameRoot);

/// What [MultiplayerGateway] does with preferences, over [MultiplayerStore].
///
/// The store is the record. A round with the game ([sync]) runs inside the
/// store's own lane: it reads the game's journal, saves the settings with the
/// game's changes and the projection's record, and writes the projection
/// before any other change reaches the settings, so no change made meanwhile,
/// a link-handling change included, is lost or written over.
///
/// While the stored settings cannot be read, nothing is written: changes are
/// refused, and a round changes neither the store nor the game's copy, which
/// would otherwise take the stand-ins.
final class MultiplayerPreferencesKeeper {
  MultiplayerPreferencesKeeper(
    this._store, {
    String? serviceOrigin,
    GameProcessProbe? probe,
    GameStagingOpener? staging,
  }) : _serviceOrigin = serviceOrigin,
       _probe = probe ?? probeLaunchProcessRunning,
       _staging = staging ?? LaunchStagingStore.new;

  final MultiplayerStore _store;
  final String? _serviceOrigin;
  final GameProcessProbe _probe;
  final GameStagingOpener _staging;

  static const _unwritable =
      'Your multiplayer settings could not be saved. Check that TopiaForge '
      'can write its data folder, then try again.';

  /// The preferences, as [MultiplayerGateway.loadPreferences] reads them.
  Future<MultiplayerPreferences> load() async =>
      _preferences(await _store.loadSettings());

  /// [MultiplayerGateway.changePreferences].
  Future<MultiplayerPreferences> change(
    MultiplayerPreferencesChange change,
  ) async {
    try {
      return await _store.withSettings((current, write) async {
        final next = change.applyTo(_preferences(current));
        final extras = current.extras;
        final kept = MultiplayerSettingsExtras(
          // Stored hosting defaults stay unstored until the player sets one,
          // so the projection keeps its own until then.
          hosting: change.hostingPort == null ? extras.hosting : next.hosting,
          networkNotice: next.networkNotice,
          revision: extras.revision,
          journalEpoch: extras.journalEpoch,
          journalApplied: extras.journalApplied,
        );
        await write(next.settings, kept);
        return next;
      });
    } on MultiplayerStoreException catch (error) {
      throw MultiplayerPreferencesException(
        error.unreadable ? error.message : _unwritable,
        unreadable: error.unreadable,
      );
    } on ArgumentError {
      throw const MultiplayerPreferencesException(
        'That value is not allowed, so nothing was changed.',
      );
    } on FormatException {
      throw const MultiplayerPreferencesException(
        'That value is not allowed, so nothing was changed.',
      );
    }
  }

  /// [MultiplayerGateway.syncWithGame].
  Future<MultiplayerPreferencesRound> sync(
    GameInstall install, {
    CancellationToken? cancellation,
  }) async {
    bool cancelled() => cancellation?.isCancelled ?? false;
    try {
      return await _store.withSettings((current, write) async {
        if (cancelled()) {
          return MultiplayerPreferencesRoundSkipped(_preferences(current));
        }
        final extras = current.extras;
        var saved = current;
        final round = await _staging(install.path).syncPreferences(
          MultiplayerPreferencesValues(
            settings: current.settings,
            hostingDefaults: extras.hosting,
            serviceOrigin: _serviceOrigin,
            revision: extras.revision,
            journalEpoch: extras.journalEpoch,
            journalApplied: extras.journalApplied,
          ),
          save: (sync) async {
            // Cancelled before anything is written: the game's copy and the
            // store stay as they are.
            if (cancelled()) throw const OperationCancelledException();
            final next = MultiplayerSettingsExtras(
              hosting: sync.hostingDefaults,
              networkNotice: extras.networkNotice,
              revision: sync.revision,
              journalEpoch: sync.journalEpoch,
              journalApplied: sync.journalApplied,
            );
            await write(sync.settings, next);
            saved = MultiplayerSettingsLoad(
              sync.settings,
              current.recovery,
              extras: next,
            );
          },
        );
        return MultiplayerPreferencesSynced(
          _preferences(saved),
          changedInGame: round.changedInGame,
        );
      });
    } on OperationCancelledException {
      return MultiplayerPreferencesRoundSkipped(await load());
    } on MultiplayerStoreException catch (error) {
      final now = await load();
      return error.unreadable
          ? MultiplayerPreferencesRoundSkipped(now)
          : MultiplayerPreferencesRoundFailed(
              now,
              MultiplayerRoundProblem.preferencesNotSaved,
            );
    } on Object {
      // The store was saved first, so the next round writes the copy.
      return MultiplayerPreferencesRoundFailed(
        await load(),
        MultiplayerRoundProblem.gameCopyNotWritten,
      );
    }
  }

  /// [MultiplayerGateway.gameRunState]. The game counts as running from
  /// [install] when a process runs from its executable.
  Future<GameRunState> gameRunState(GameInstall install) async {
    try {
      return switch (await _probe(install.executablePath)) {
        true => GameRunState.running,
        false => GameRunState.notRunning,
        null => GameRunState.unknown,
      };
    } on Object {
      return GameRunState.unknown;
    }
  }

  static MultiplayerPreferences _preferences(MultiplayerSettingsLoad load) =>
      MultiplayerPreferences(
        settings: load.settings,
        hosting: load.extras.hosting,
        networkNotice: load.extras.networkNotice,
        health: switch (load.recovery) {
          MultiplayerStoreRecovery.none => MultiplayerPreferencesHealth.sound,
          MultiplayerStoreRecovery.salvaged =>
            MultiplayerPreferencesHealth.salvaged,
          MultiplayerStoreRecovery.reset => MultiplayerPreferencesHealth.reset,
          MultiplayerStoreRecovery.unreadable =>
            MultiplayerPreferencesHealth.unreadable,
        },
      );
}
