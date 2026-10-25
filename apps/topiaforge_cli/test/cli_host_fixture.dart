import 'dart:async';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge/src/cli_host_options.dart';
import 'package:topiaforge/src/cli_host_workflow.dart';

final class HostFixture implements LauncherRepository, MultiplayerRepository {
  final controller = StreamController<LaunchActivity>.broadcast(sync: true);
  final messages = <String>[];
  final prompts = <String>[];
  final profile = LauncherProfile.defaultProfile();
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  var settings = MultiplayerSettings();
  var hosting = MultiplayerHostingDefaults.forPlayer('Player');
  GameRunState running = GameRunState.notRunning;
  bool readable = true, syncFails = false, blocked = false, starts = true;
  bool consent = true, terminal = false, developer = false;
  bool throwLaunch = false, throwPreview = false;
  var launches = 0, syncs = 0;
  void Function()? onLaunch, onConsent;
  LaunchSessionRequest? request;
  MultiplayerServiceFeatures? features = const MultiplayerServiceFeatures(
    hosting: true,
    invites: true,
    signaling: true,
    relay: true,
    killSwitch: false,
    heartbeat: Duration(seconds: 10),
    listingTtl: Duration(seconds: 30),
    relayCredentialTtl: Duration(minutes: 5),
  );

  Future<int> run([List<String> args = const ['--yes']]) => runCliHost(
    launcher: this,
    multiplayer: this,
    install: install,
    profile: profile,
    developerMode: developer,
    options: CliHostOptions(args),
    hasTerminal: terminal,
    confirm: (prompt) async {
      prompts.add(prompt);
      onConsent?.call();
      return consent;
    },
    write: messages.add,
    launch: start,
    serviceOrigin: 'https://play.example',
    newIntent: () => 'intent-one',
  );
  @override
  Stream<LaunchActivity> get launchActivities => controller.stream;
  @override
  Future<GameRunState> gameRunState(GameInstall install) async => running;
  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall install, {
    CancellationToken? cancellation,
  }) async {
    syncs++;
    final prefs = MultiplayerPreferences(
      settings: settings,
      hosting: hosting,
      health: readable
          ? MultiplayerPreferencesHealth.sound
          : MultiplayerPreferencesHealth.unreadable,
    );
    return syncFails
        ? MultiplayerPreferencesRoundSkipped(prefs)
        : MultiplayerPreferencesSynced(prefs, changedInGame: false);
  }

  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) async {
    final updated = change.applyTo(
      MultiplayerPreferences(settings: settings, hosting: hosting),
    );
    settings = updated.settings;
    return updated;
  }

  @override
  Future<MultiplayerServiceCheck> readServiceStatus({
    CancellationToken? cancellation,
  }) async => features == null
      ? const MultiplayerServiceCheck.unavailable(
          MultiplayerServiceProblem.unreachable,
          'Unreachable',
        )
      : MultiplayerServiceCheck.available(features!);
  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    if (throwPreview) throw StateError('private endpoint details');
    return LaunchPreview(
      profileId: profile.id,
      profileRevision: profile.revision,
      installIdentity: 'install',
      packageDigest: 'digest',
      requestedSelection: selectionOverride ?? profile.launchSelection,
      effectiveSelection: const LaunchSelection.mainMenu(),
      issues: blocked
          ? const [
              LauncherIssue(severity: IssueSeverity.error, message: 'blocked'),
            ]
          : const [],
    );
  }

  Future<LaunchResult> start(LaunchSessionRequest value) async {
    launches++;
    request = value;
    if (throwLaunch) throw StateError('private endpoint details');
    onLaunch?.call();
    return LaunchResult(
      started: starts,
      message: 'private endpoint details',
      requestId: 'request-one',
      latestActivity: base(),
    );
  }

  LaunchActivity base({
    String requestId = 'request-one',
    String digest = 'digest',
    String installId = 'install',
    int? revision,
  }) => LaunchActivity(
    requestId: requestId,
    profileId: profile.id,
    profileRevision: revision ?? profile.revision,
    installIdentity: installId,
    packageDigest: digest,
    command: 'host-session',
  );

  LaunchActivity success({
    String requestId = 'request-one',
    String intent = 'intent-one',
    String? invite = '0123456789',
    int sequence = 2,
    String digest = 'digest',
  }) => base(requestId: requestId, digest: digest)
      .applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: requestId,
          command: 'host-session',
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
          multiplayer: LaunchOutcomeMultiplayer(
            role: LaunchSessionRole.host,
            intentId: intent,
            serverId: 'a' * 64,
          ),
        ),
      )
      .applyProgress(
        LaunchProgress(
          requestId: requestId,
          sequence: sequence,
          phase: 'idle',
          session: LaunchProgressSession(
            role: LaunchSessionRole.host,
            intentId: intent,
            state: LaunchSessionState.hosting,
            serverId: 'a' * 64,
            players: 1,
            maxPlayers: 8,
            inviteCode: invite,
          ),
        ),
      );

  LaunchActivity failure(LaunchMultiplayerFailure failure) =>
      base().applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request-one',
          command: 'host-session',
          sequence: 1,
          status: 'failed',
          phase: 'idle',
          error: LaunchOperationError(
            code: 'external',
            message: 'private endpoint details',
          ),
          multiplayer: LaunchOutcomeMultiplayer(
            role: LaunchSessionRole.host,
            intentId: 'intent-one',
            failure: failure,
            detail: 'private endpoint details',
          ),
        ),
      );

  @override
  Future<void> dispose() => controller.close();
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
