part of 'widget_test.dart';

/// The bloc started over a gateway that holds every answer, with its first
/// read, game check and round answered: [round] answers the round.
Future<(LauncherBloc, _FakeMultiplayerGateway)> _settledMultiplayerBloc({
  SessionLinkHandlingGateway? links,
  MultiplayerPreferencesRound? round,
}) async {
  final gateway = _FakeMultiplayerGateway()..hold = true;
  final bloc = _multiplayerBloc(gateway, links: links);
  await _idle();
  gateway.heldLoads.single.complete(_named('Sam'));
  gateway.heldGames.single.complete(GameRunState.notRunning);
  await _idle();
  gateway.heldRounds.single.complete(
    round ?? MultiplayerPreferencesSynced(_named('Sam'), changedInGame: false),
  );
  await _idle();
  return (bloc, gateway);
}

/// A change of the display name, as Settings saves it.
const _renamed = MultiplayerPreferencesEdited(
  MultiplayerPreferencesChange(displayName: 'Robin'),
);

/// Races between the game starting or closing, the checks that see it, the
/// changes the player makes and the rounds that bring the game its copy.
void registerMultiplayerRaceTests() {
  MultiplayerSettingsView view(LauncherBloc bloc) =>
      bloc.state.multiplayer.settings;

  group('Settings → Multiplayer, in the bloc, in a race', () {
    test('a check asked before a change that found the game running never '
        'lifts the lock', () async {
      final (bloc, gateway) = await _settledMultiplayerBloc();
      // A check reads the process list before the game starts.
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      // The game starts, and a change asks again first and finds it.
      bloc.add(_renamed);
      await _idle();
      expect(gateway.heldGames, hasLength(3));
      gateway.heldGames[2].complete(GameRunState.running);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);
      // The older check answers last, from before the game started.
      gateway.heldGames[1].complete(GameRunState.notRunning);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);
      expect(gateway.heldRounds, hasLength(1));

      // Closed, a new check lifts the lock and takes in the game's changes.
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      expect(gateway.heldRounds, hasLength(2));
    });

    test('a change saved as the game starts reaches the game once it is '
        'closed, never while it runs', () async {
      final (bloc, gateway) = await _settledMultiplayerBloc();
      gateway.changeGate = Completer<void>();
      bloc.add(_renamed);
      await _idle();
      // The change's own check: the game has not started yet.
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      // While the change is written, the game starts and a check finds it.
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      gateway.heldGames.last.complete(GameRunState.running);
      await _idle();
      gateway.changeGate!.complete();
      await _idle();
      expect(view(bloc).preferences!.settings.displayName, 'Robin');
      // A fresh check decides, and the game still runs.
      expect(gateway.heldGames, hasLength(4));
      gateway.heldGames.last.complete(GameRunState.running);
      await _idle();
      expect(gateway.heldRounds, hasLength(1));
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);

      // Closed, the round brings the game its copy with the change.
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      expect(gateway.heldRounds, hasLength(2));
    });

    test('a change a round overtook still reaches the game', () async {
      // The first round fails, so a round is still due when the player
      // changes something.
      final (bloc, gateway) = await _settledMultiplayerBloc(
        round: MultiplayerPreferencesRoundFailed(
          _named('Sam'),
          MultiplayerRoundProblem.gameCopyNotWritten,
        ),
      );
      gateway.changeGate = Completer<void>();
      bloc.add(_renamed);
      await _idle();
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      // A check asked meanwhile runs the due round before the change is
      // written, so that round's copy lacks it.
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      gateway.heldRounds.last.complete(
        MultiplayerPreferencesSynced(_named('Sam'), changedInGame: false),
      );
      await _idle();
      gateway.changeGate!.complete();
      await _idle();
      // The change is in, so the game is checked again and given its copy.
      expect(gateway.heldGames.last.isCompleted, isFalse);
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      expect(gateway.heldRounds, hasLength(3));
    });

    test('a link choice saved during a round still shows once the round '
        'ends', () async {
      final links = _FakeSessionLinkHandling();
      final (bloc, gateway) = await _settledMultiplayerBloc(links: links);
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(upnp: true),
        ),
      );
      await _idle();
      gateway.heldGames.last.complete(GameRunState.notRunning);
      await _idle();
      expect(view(bloc).syncing, isTrue);
      // The round read the store first; the link choice is written after it.
      final read = gateway.preferences;
      bloc.add(const SessionLinkHandlingChanged(false));
      await _idle();
      gateway.heldRounds.last.complete(
        MultiplayerPreferencesSynced(read, changedInGame: false),
      );
      await _idle();
      expect(view(bloc).preferences!.settings.linkHandlingEnabled, isFalse);
      expect(view(bloc).preferences!.settings.upnpEnabled, isTrue);

      // What is read after the choice is the store's own, and shows as it
      // is, as when the file was damaged since and reset to the defaults.
      gateway.hold = false;
      bloc.add(const MultiplayerPreferencesRefreshRequested(checkGame: false));
      await _idle();
      expect(view(bloc).preferences!.settings.linkHandlingEnabled, isTrue);
    });
  });
}
