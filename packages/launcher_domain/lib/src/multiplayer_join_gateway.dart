/// What the launcher's join flow asks of the data layer once a player wants to
/// join a server: every stage the launcher answers, from resolving the target
/// to starting the game with the join, the game's own requests to join, and
/// who runs the game now.
///
/// The bloc depends on [MultiplayerJoinGateway]; `launcher_data` implements it
/// in its multiplayer facade, which hands the stages that touch the game's
/// files and processes to the launcher repository's [MultiplayerSessionHost].
/// Nothing either returns names a network address: a direct target's host and
/// port exist only in the join target the player gave and in the game's own
/// launch request.
library;

import 'cancellation.dart';
import 'launch_resolution.dart';
import 'models.dart';
import 'multiplayer.dart';
import 'multiplayer_gateway.dart';

/// Whether a game runs from an install, as a join needs to know before the
/// player consents and before it starts the game.
enum JoinGameRunning {
  /// No game runs from the install.
  none,

  /// A game this launcher started runs, and its process is verified. A join
  /// may join it live, when it already runs the server's mods, or ask it to
  /// quit, as its QUIT button would, and start it again for the server.
  owned,

  /// A game runs that this launcher did not start or cannot verify, or the
  /// launcher cannot tell. The player closes it; a join never does.
  foreign,
}

/// How the launching stage went.
sealed class JoinLaunch {
  const JoinLaunch();
}

/// The game took the join: a new process started with the join's launch
/// request, or, for a game this launcher started that already runs the
/// server's mods, a live join was staged for it.
///
/// The game reports the join under [requestId], as progress and outcomes V2
/// that `LauncherRepository.launchActivities` carries: the launch request's
/// id, or a live join's own.
final class JoinLaunchStarted extends JoinLaunch {
  const JoinLaunchStarted({
    required this.requestId,
    required this.live,
    this.activity,
    this.replacedGame,
  });

  final String requestId;

  /// Whether the join went to the running game live, without a restart.
  final bool live;

  /// The join's activity as it began, when there is one.
  final LaunchActivity? activity;

  /// How a game this launcher started ended before the new one started, when
  /// the launch closed one.
  final ReplacedGame? replacedGame;

  @override
  String toString() => 'join launch (${live ? 'live' : 'started'})';
}

/// Nothing started: [failure] is what the join reports, at the launching
/// stage.
final class JoinLaunchRefused extends JoinLaunch {
  const JoinLaunchRefused(this.failure);

  final JoinFailed failure;

  @override
  String toString() => 'join launch refused (${failure.code.name})';
}

/// The launcher closed while the launch waited, so nothing started and
/// nothing was stopped.
final class JoinLaunchStopped extends JoinLaunch {
  const JoinLaunchStopped();

  @override
  String toString() => 'join launch stopped';
}

/// A game this launcher started asked it to run the join flow
/// (`session-handoff`), for [install]. The handoff names an invite, a listing,
/// or the game's own join, planned again; never an address.
final class JoinHandoffArrival {
  const JoinHandoffArrival({required this.install, required this.handoff});

  final GameInstall install;
  final SessionHandoff handoff;

  /// The target's kind only: an invite's code lets anyone join.
  @override
  String toString() => 'session handoff (${handoff.target.kind})';
}

/// What the launcher's join flow asks of multiplayer, beside the preferences
/// and network checks of [MultiplayerGateway].
///
/// A gateway that offers it is tested for with `is`; a launcher without it
/// cannot join.
abstract interface class MultiplayerJoinGateway implements MultiplayerGateway {
  /// Answers [state] when it waits on the launcher, so every event the
  /// reducer takes comes from one place: resolving, fetching the descriptor,
  /// checking (a plan from the exact archives [install] holds and the player's
  /// sources offer, with [developerMode]), downloading, installing into
  /// [install] and preparing the server profile. Progress goes to
  /// [onProgress] as the reducer's own events.
  ///
  /// Null for any other stage, and when [cancellation] stopped the work.
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
    GameInstall? install,
    void Function(JoinEvent progress)? onProgress,
    bool developerMode = false,
  });

  /// Whether a game runs from [install], and whether this launcher started
  /// it. Never throws.
  Future<JoinGameRunning> joinGameRunning(GameInstall install);

  /// Answers a join in [JoinStage.launching]: brings the copy of the
  /// preferences the game reads up to date, then starts the plan's server
  /// profile with launch request V5 under [intentId], or stages a live join
  /// for a game this launcher started whose mods already match. A game this
  /// launcher started that runs other mods is asked to quit first, as its
  /// QUIT button would, when the consent covered closing it. Never throws.
  Future<JoinLaunch> launchJoin(
    JoinSessionState state, {
    required GameInstall install,
    required String intentId,
    bool developerMode = false,
  });

  /// Withdraws [launch] when the game has not taken it yet, and says whether
  /// it did: only a live join can be withdrawn, so a started process always
  /// answers false. Never throws.
  Future<bool> withdrawJoinLaunch(
    GameInstall install,
    JoinLaunchStarted launch,
  );

  /// The joins games this launcher started ask it to run.
  Stream<JoinHandoffArrival> get handoffs;

  /// Moves a joined server to the front of the player's history. Throws when
  /// the history cannot be written.
  Future<MultiplayerSettings> recordJoin({
    required String serverId,
    required String name,
  });
}

/// Optional launcher-repository capability for the join stages that read the
/// install to plan, and start or follow the game, beside the content stages of
/// `MultiplayerContentHost`. Callers test for it with `is`.
abstract interface class MultiplayerSessionHost {
  /// Answers a join in [JoinStage.checking]: plans it from the exact archives
  /// [install] holds and the player's sources offer
  /// (`JoinPlanner.planFromCatalog`), for this install's game build and the
  /// bundled runtime. A plan made right before consent is the one consent
  /// shows. Null for any other stage.
  Future<JoinEvent?> planJoinContent(
    GameInstall install,
    JoinSessionState state, {
    bool developerMode = false,
  });

  /// Whether a game runs from [install], and whether this launcher started
  /// it and can verify its process. Never throws.
  Future<JoinGameRunning> joinGameRunning(GameInstall install);

  /// Answers a join in [JoinStage.launching] with [session], its join
  /// request:
  ///
  /// - a game this launcher started that runs exactly the server profile's
  ///   packages, for a target that is not an address, gets a live join;
  /// - one that runs other packages is asked to quit, as its QUIT button
  ///   would, when [JoinSessionState.closeOwnedGameAcknowledged] says the
  ///   consent covered it; otherwise the join ends as
  ///   [JoinFailureCode.gameRunning], and so it does for a game this launcher
  ///   did not start;
  /// - a game whose next start opens in safe mode, where a session is
  ///   refused, ends the join before anything starts;
  /// - otherwise [beforeStart] runs, once no game runs, and the server
  ///   profile starts with [session], unless [beforeStart] answered a
  ///   failure, which the join then reports and nothing starts.
  ///
  /// Never throws.
  Future<JoinLaunch> launchJoinSession(
    GameInstall install,
    JoinSessionState state,
    LaunchSessionRequest session, {
    Future<JoinFailed?> Function()? beforeStart,
  });

  /// [MultiplayerJoinGateway.withdrawJoinLaunch].
  Future<bool> withdrawJoinLaunch(
    GameInstall install,
    JoinLaunchStarted launch,
  );

  /// The handoffs the games this launcher started stage for it, each taken
  /// once.
  Stream<JoinHandoffArrival> get sessionHandoffs;
}
