part of 'launcher_multiplayer_state.dart';

/// Where a join came from. Every way in reaches the same flow, and none of
/// them joins before the player consents.
enum JoinFlowOrigin {
  /// A `topiaforge://join/…` link.
  link,

  /// The server browser's JOIN.
  browser,

  /// An address typed in the direct-connect dialog.
  address,

  /// The game, through a `session-handoff`.
  handoff,
}

/// A question the flow asks before it contacts anyone, in the approved
/// wording.
enum JoinQuestion {
  /// "Your address and multiplayer": before the player's first join.
  disclosure,

  /// "Allow a direct connection?": whenever the target can only be reached
  /// directly. Cancel is its primary button.
  directConnection,
}

/// Why a flow stopped before its join began.
enum JoinFlowProblem {
  /// No Robotopia install is selected, so there is nothing to join with.
  noInstall,

  /// The multiplayer settings could not be read just now.
  settingsUnreadable,

  /// The disclosure was read, but that could not be saved.
  disclosureNotSaved,
}

/// One join, from the questions asked before anything is contacted until
/// the player closes its outcome.
///
/// [target] can hold an address the player typed, or an invite code; nothing
/// here prints it, and `toString` names only the origin and the stage.
final class JoinFlow {
  const JoinFlow({
    required this.id,
    required this.origin,
    required this.target,
    this.privacy = MultiplayerAddressPrivacy.relayOnly,
    this.trustedServerId,
    this.question,
    this.problem,
    this.join,
    this.plan,
    this.gameRunning,
    this.launch,
    this.voice = false,
    this.hostServedTicked = false,
    this.waitingLong = false,
    this.closed = false,
  });

  /// Counts flows, so an answer for an older one is dropped.
  final int id;
  final JoinFlowOrigin origin;
  final JoinTarget target;

  /// The privacy chosen for this session before it started: relay-only,
  /// unless the player allowed a direct connection for it.
  final MultiplayerAddressPrivacy privacy;

  /// A server the player chose to trust after an identity change.
  final String? trustedServerId;

  /// The question waiting for an answer before anything is contacted.
  final JoinQuestion? question;

  /// Why the flow stopped before its join began.
  final JoinFlowProblem? problem;

  /// The join, once the questions are answered.
  final JoinSessionState? join;

  /// The newest plan, kept for a failure found while checking, whose join
  /// holds none.
  final JoinPlan? plan;

  /// Who runs the game, as the consent sheet says it; null until checked.
  final JoinGameRunning? gameRunning;

  /// How the game took the join, once it did.
  final JoinLaunchStarted? launch;

  /// Whether the player's voice chat is on, with consent, when the join
  /// started; the consent sheet's voice line says it with the server's own.
  final bool voice;

  /// Whether the player ticked the consent sheet's developer-mode box for
  /// the mods only the host has. It counts only for the consent on screen:
  /// the join forgets it as soon as it leaves that consent.
  final bool hostServedTicked;

  /// Whether the game has not answered a started join for a while.
  final bool waitingLong;

  /// Whether the dialog closed: the player closed the outcome, or the join
  /// reached the session, where the game takes over.
  final bool closed;

  /// Whether the flow shows its dialog.
  bool get showsDialog => !closed;

  /// The server's name as its host wrote it, without addresses, once known.
  String? get serverName {
    final name = join?.descriptor?.descriptor.name;
    return name == null ? null : withoutAddresses(name);
  }

  JoinFlow copyWith({
    MultiplayerAddressPrivacy? privacy,
    String? trustedServerId,
    JoinQuestion? question,
    bool clearQuestion = false,
    JoinFlowProblem? problem,
    bool clearProblem = false,
    JoinSessionState? join,
    bool clearJoin = false,
    JoinPlan? plan,
    JoinGameRunning? gameRunning,
    bool clearGameRunning = false,
    JoinLaunchStarted? launch,
    bool clearLaunch = false,
    bool? voice,
    bool? hostServedTicked,
    bool? waitingLong,
    bool? closed,
  }) => JoinFlow(
    id: id,
    origin: origin,
    target: target,
    privacy: privacy ?? this.privacy,
    trustedServerId: trustedServerId ?? this.trustedServerId,
    question: clearQuestion ? null : question ?? this.question,
    problem: clearProblem ? null : problem ?? this.problem,
    join: clearJoin ? null : join ?? this.join,
    plan: plan ?? this.plan,
    gameRunning: clearGameRunning ? null : gameRunning ?? this.gameRunning,
    launch: clearLaunch ? null : launch ?? this.launch,
    voice: voice ?? this.voice,
    hostServedTicked: hostServedTicked ?? this.hostServedTicked,
    waitingLong: waitingLong ?? this.waitingLong,
    closed: closed ?? this.closed,
  );

  @override
  String toString() =>
      'join flow $id (${origin.name}, ${join?.stage.name ?? question?.name ?? problem?.name ?? 'preparing'})';
}

/// The join flow's part of the launcher's state.
final class MultiplayerJoinView {
  const MultiplayerJoinView({this.flow, this.typedAddresses = const []});

  /// The join that runs or shows its outcome; null when there is none.
  final JoinFlow? flow;

  /// The addresses the player typed in the direct-connect dialog, newest
  /// first, for its history rows. They stay in memory while the launcher
  /// runs: nothing writes them anywhere, and only that dialog shows them.
  final List<String> typedAddresses;

  /// How many typed addresses the dialog remembers.
  static const typedAddressLimit = 6;

  MultiplayerJoinView copyWith({
    JoinFlow? flow,
    bool clearFlow = false,
    List<String>? typedAddresses,
  }) => MultiplayerJoinView(
    flow: clearFlow ? null : flow ?? this.flow,
    typedAddresses: typedAddresses ?? this.typedAddresses,
  );

  @override
  String toString() => 'join view (${flow ?? 'idle'})';
}
