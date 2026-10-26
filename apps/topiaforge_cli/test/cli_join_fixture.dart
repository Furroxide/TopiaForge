import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge/src/cli_join_options.dart';
import 'package:topiaforge/src/cli_join_workflow.dart';
import '../../../packages/launcher_domain/test/multiplayer_join_support.dart';

export '../../../packages/launcher_domain/test/multiplayer_join_support.dart';

final class JoinFixture implements LauncherRepository, MultiplayerJoinGateway {
  final controller = StreamController<LaunchActivity>.broadcast(sync: true);
  final cancel = Completer<void>();
  final messages = <String>[], prompts = <String>[];
  final steps = <JoinStage>[];
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  late JoinPlan plan;
  MultiplayerSettings settings = MultiplayerSettings();
  JoinGameRunning running = JoinGameRunning.none;
  bool terminal = false, agree = true, developer = false, failSync = false;
  bool readable = true, live = false, withdrawn = true, automaticSuccess = true;
  int launches = 0, answers = 0, changes = 0, withdrawals = 0, histories = 0;
  int intents = 0;
  String intent = '';
  JoinSessionState? launchState;
  Future<void> Function(JoinStage)? beforeAnswer;
  Future<void> Function()? beforeLaunch;
  void Function(String)? onConsent;
  JoinFailed? failure;
  JoinStage? failureStage;
  JoinLaunch? launchResult;
  LaunchActivity Function()? activityOnLaunch;
  bool throwAnswer = false, throwHistory = false;

  Future<void> initialize({bool downloads = true, bool hosted = false}) async {
    plan = await planJoin(
      [
        (
          testPackage('example.sandbox'),
          hosted
              ? JoinPackageResolution.hostOffered()
              : downloads
              ? availableOf(
                  testManifest('example.sandbox', capabilities: ['physics']),
                )
              : installedOf(
                  testManifest('example.sandbox', capabilities: ['physics']),
                ),
        ),
      ],
      environment: environmentWith(developerMode: hosted),
      hostServesPackages: hosted,
    );
  }

  Future<int> run([List<String> args = const ['0123456789', '--yes']]) =>
      runCliJoin(
        launcher: this,
        multiplayer: this,
        install: install,
        profileIds: {},
        developerMode: developer,
        options: CliJoinOptions(args),
        hasTerminal: terminal,
        confirm: (prompt) async {
          prompts.add(prompt);
          onConsent?.call(prompt);
          return agree;
        },
        write: messages.add,
        cancelled: cancel.future,
        newIntent: () => intent = 'intent-${++intents}',
      );
  @override
  Stream<LaunchActivity> get launchActivities => controller.stream;
  @override
  Future<JoinGameRunning> joinGameRunning(GameInstall _) async => running;
  @override
  Future<MultiplayerPreferences> loadPreferences() async =>
      MultiplayerPreferences(
        settings: settings,
        health: readable
            ? MultiplayerPreferencesHealth.sound
            : MultiplayerPreferencesHealth.unreadable,
      );
  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall _, {
    CancellationToken? cancellation,
  }) async => failSync
      ? MultiplayerPreferencesRoundSkipped(await loadPreferences())
      : MultiplayerPreferencesSynced(
          await loadPreferences(),
          changedInGame: false,
        );
  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) async {
    changes++;
    final next = change.applyTo(await loadPreferences());
    settings = next.settings;
    return next;
  }

  @override
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
    GameInstall? install,
    void Function(JoinEvent)? onProgress,
    bool developerMode = false,
  }) async {
    answers++;
    steps.add(state.stage);
    await beforeAnswer?.call(state.stage);
    if (throwAnswer) throw StateError('private 203.0.113.99 endpoint');
    if (state.stage == failureStage) return failure;
    // An install drains and returns its terminal event even after cancellation.
    return switch (state.stage) {
      JoinStage.resolving => JoinTargetResolved(plan.server.serverId),
      JoinStage.fetchingDescriptor => JoinDescriptorVerified(plan.server),
      JoinStage.checking => JoinPlanReady(plan),
      JoinStage.downloading => const JoinDownloadsCompleted(),
      JoinStage.installing => const JoinInstallCompleted(),
      JoinStage.preparingProfile => const JoinProfilePrepared(),
      _ => null,
    };
  }

  @override
  Future<JoinLaunch> launchJoin(
    JoinSessionState state, {
    required GameInstall install,
    required String intentId,
    bool developerMode = false,
  }) async {
    launches++;
    launchState = state;
    intent = intentId;
    await beforeLaunch?.call();
    if (launchResult != null) return launchResult!;
    if (automaticSuccess) controller.add(activityOnLaunch?.call() ?? success());
    return JoinLaunchStarted(
      requestId: 'request-$intents',
      live: live,
      activity: base(),
    );
  }

  @override
  Future<bool> withdrawJoinLaunch(
    GameInstall _,
    JoinLaunchStarted launch,
  ) async {
    withdrawals++;
    return withdrawn;
  }

  @override
  Future<MultiplayerSettings> recordJoin({
    required String serverId,
    required String name,
  }) async {
    histories++;
    if (throwHistory) throw StateError('private path');
    return settings;
  }

  LaunchActivity base({
    String? request,
    String? profile,
    int revision = 2,
    String digest = 'digest',
    String installId = 'install',
    String command = 'join-session',
    int pid = 123,
    int seconds = 0,
    String executable = 'fixture/game.exe',
    String native = 'token',
  }) => LaunchActivity(
    requestId: request ?? 'request-$intents',
    profileId: profile ?? plan.derivedProfile.id,
    profileRevision: revision,
    installIdentity: installId,
    packageDigest: digest,
    command: command,
    process: LaunchProcessIdentity(
      pid: pid,
      startTimeUtc: DateTime.utc(2026, 1, 1, 0, 0, seconds),
      executablePath: executable,
      nativeStartToken: native,
    ),
  );
  LaunchActivity success({LaunchActivity? activity, String? forIntent}) {
    final result = activity ?? base();
    return result.applyOutcome(
      LaunchOutcome(
        kind: 'launch',
        requestId: result.requestId,
        command: 'join-session',
        sequence: 1,
        status: 'succeeded',
        phase: 'idle',
        multiplayer: LaunchOutcomeMultiplayer(
          role: LaunchSessionRole.join,
          intentId: forIntent ?? intent,
          serverId: plan.server.serverId,
        ),
      ),
    );
  }

  @override
  Future<void> dispose() => controller.close();
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
