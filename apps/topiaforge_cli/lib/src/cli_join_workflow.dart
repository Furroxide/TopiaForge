import 'dart:async';
import 'dart:convert';
import 'dart:math';
import 'package:launcher_domain/launcher_domain.dart';
import 'cli_join_consent.dart';
import 'cli_join_options.dart';
import 'cli_join_receipt.dart';
import 'cli_join_status.dart';
import 'cli_multiplayer_text.dart';

/// Runs the launcher's reducer and gateway, including its deferred cancellation
/// and one admission re-plan. A spawned game is never killed by stopping the CLI.
Future<int> runCliJoin({
  required LauncherRepository launcher,
  required MultiplayerJoinGateway multiplayer,
  required GameInstall install,
  required Set<String> profileIds,
  required bool developerMode,
  required CliJoinOptions options,
  required bool hasTerminal,
  required Future<bool> Function(String prompt) confirm,
  required void Function(String) write,
  Future<void>? cancelled,
  String Function()? newIntent,
}) async {
  final knownProfileIds = Set<String>.of(profileIds);
  var stage = 'TFJOIN100';
  var stopped = false, finished = false;
  JoinSessionState? join;
  JoinLaunchStarted? started;
  CliJoinReceipt? receipt;
  StreamSubscription<LaunchActivity>? subscription;
  final token = CancellationToken();
  final stop = Completer<void>();
  Timer? timer;
  var deadline = Completer<void>();
  if (cancelled != null) {
    unawaited(
      cancelled.then((_) {
        if (finished) return;
        stopped = true;
        token.cancel();
        if (join case final state?) {
          join = JoinSession.reduce(state, const JoinCancelRequested());
        }
        if (!stop.isCompleted) stop.complete();
      }),
    );
  }
  int refuse(String words, [int code = 1]) {
    write('$stage: $words');
    return code;
  }

  Future<int> cancel() async {
    final launch = started;
    final withdrawn =
        launch != null &&
        launch.live &&
        await multiplayer.withdrawJoinLaunch(install, launch);
    return refuse(
      launch == null || withdrawn
          ? 'Join cancelled. No further work will start; run join again when ready.'
          : 'Stopped following the join. Robotopia was left running and may still join; check the game before retrying.',
      130,
    );
  }

  Future<bool> consent(String prompt) async =>
      !stopped && (hasTerminal ? await confirm(prompt) : options.yes);
  void take(JoinEvent event) {
    if (join case final state?) join = JoinSession.reduce(state, event);
  }

  try {
    if (!hasTerminal && !options.yes) {
      return refuse(
        'Joining needs consent. Read topiaforge join --help and rerun with --yes when there is no terminal.',
        2,
      );
    }
    if (options.allowHostPackages && !developerMode) {
      return refuse(
        'Mods from the host require developer mode. Enable it in the launcher or omit --allow-host-packages.',
        2,
      );
    }
    stage = 'TFJOIN110';
    final running = await multiplayer.joinGameRunning(install);
    if (stopped) return await cancel();
    if (running == JoinGameRunning.foreign) {
      return refuse(JoinConsentText.foreignGame);
    }
    final round = running == JoinGameRunning.none
        ? await multiplayer.syncWithGame(install, cancellation: token)
        : null;
    var preferences = round?.preferences ?? await multiplayer.loadPreferences();
    if (stopped) return await cancel();
    if (!preferences.readable ||
        (round != null && round is! MultiplayerPreferencesSynced)) {
      return refuse(
        'Multiplayer settings could not be read or synchronized. Close programs using the game data and try again.',
      );
    }
    if (preferences.settings.needsPrivacyDisclosure) {
      for (final line in CliMultiplayerText.disclosure) {
        write(line);
      }
      if (!await consent('I understand [y/N]') || stopped) {
        return await cancel();
      }
      preferences = await multiplayer.changePreferences(
        const MultiplayerPreferencesChange(acknowledgeDisclosure: true),
      );
      if (!preferences.readable ||
          preferences.settings.needsPrivacyDisclosure) {
        return refuse(
          'The address disclosure could not be saved. Check Multiplayer settings and try again.',
        );
      }
    }
    if (options.allowDirect) {
      write('Allow a direct connection?');
      write(CliMultiplayerText.direct);
      if (!await consent('Allow for this session [y/N]') || stopped) {
        return await cancel();
      }
    }
    final consentSettings = jsonEncode(preferences.settings.toJson());
    join = JoinSession.start(
      options.target,
      privacy: preferences.settings.privacyForSession(
        allowDirect: options.allowDirect,
      ),
    );
    subscription = launcher.launchActivities.listen(
      (activity) => receipt?.accept(activity),
      onError: (Object _) {
        if (!deadline.isCompleted) deadline.complete();
      },
    );
    while (true) {
      final state = join!;
      if (state.isEnded) {
        final failure = state.outcome?.failure;
        if (failure != null) stage = cliJoinStage(failure.stage);
        if (failure?.code == JoinFailureCode.cancelled || stopped) {
          return await cancel();
        }
        return refuse(
          failure == null
              ? 'The session ended before this join completed. Ask the host for a new invite.'
              : cliJoinFailure(failure, started: started != null),
        );
      }
      stage = cliJoinStage(state.stage);
      if (state.stage == JoinStage.inSession) {
        write('$stage: Joined the session. Robotopia continues running.');
        try {
          await multiplayer.recordJoin(
            serverId: state.serverId!,
            name: state.descriptor!.descriptor.name,
          );
        } on Object {
          write(
            'The recent-server entry could not be saved. The game remains joined.',
          );
        }
        return 0;
      }
      if (stopped && !JoinSession.deferredCancelStages.contains(state.stage)) {
        return await cancel();
      }
      if (state.stage == JoinStage.awaitingConsent) {
        final nowRunning = await multiplayer.joinGameRunning(install);
        if (stopped) return await cancel();
        for (final line in cliJoinConsent(
          state,
          voice: preferences.settings.voiceActive,
          profileIds: knownProfileIds,
          running: nowRunning,
        )) {
          write(line);
        }
        if (nowRunning == JoinGameRunning.foreign) {
          return refuse(JoinConsentText.foreignGame);
        }
        final hosted = state.plan!.hasUnverifiedPackages;
        if (hosted && !options.allowHostPackages) {
          return refuse(
            'These mods need your separate permission. Review them and rerun with --allow-host-packages in developer mode.',
            2,
          );
        }
        if (hosted && !await consent('${JoinHostServedText.checkbox} [y/N]')) {
          return await cancel();
        }
        if (!await consent('Join [y/N]') || stopped) return await cancel();
        final current = await multiplayer.loadPreferences();
        if (!current.readable ||
            jsonEncode(current.settings.toJson()) != consentSettings) {
          return refuse(
            'Multiplayer settings changed while you reviewed the join. Run join again to review the new settings.',
          );
        }
        take(
          JoinConsentGiven(
            hostServedAcknowledged: hosted,
            closeOwnedGameAcknowledged: nowRunning == JoinGameRunning.owned,
          ),
        );
        continue;
      }
      if (state.stage == JoinStage.launching && receipt == null) {
        if (stopped) return await cancel();
        final current = await multiplayer.loadPreferences();
        if (stopped) return await cancel();
        if (!current.readable ||
            jsonEncode(current.settings.toJson()) != consentSettings) {
          return refuse(
            'Multiplayer settings changed after consent. Run join again to review them before starting the game.',
          );
        }
        final intent =
            newIntent?.call() ??
            'join-${DateTime.now().microsecondsSinceEpoch}-${Random.secure().nextInt(1 << 32)}';
        receipt = CliJoinReceipt(intent, state.plan!.derivedProfile.id);
        write('$stage: Starting Robotopia for the reviewed server.');
        final launch = await multiplayer.launchJoin(
          join!,
          install: install,
          intentId: intent,
          developerMode: developerMode,
        );
        switch (launch) {
          case JoinLaunchStarted():
            started = launch;
            if (stopped) return await cancel();
            if (!receipt.bind(launch)) {
              return refuse(
                'The game started, but its join receipt could not be verified. Check Robotopia before retrying; it was left running.',
                3,
              );
            }
            timer?.cancel();
            deadline = Completer<void>();
            timer = Timer(options.waitTimeout, () {
              if (!deadline.isCompleted) deadline.complete();
            });
          case JoinLaunchRefused():
            take(launch.failure);
          case JoinLaunchStopped():
            return await cancel();
        }
        continue;
      }
      if (state.stage == JoinStage.launching ||
          state.stage == JoinStage.connecting ||
          state.stage == JoinStage.synchronizing) {
        final monitor = receipt!;
        final changed = monitor.changed;
        for (final event in joinEventsFor(state, monitor.current!)) {
          take(event);
        }
        if (join!.stage != state.stage || join!.isEnded) {
          if (join!.stage == JoinStage.fetchingDescriptor) {
            receipt = null;
            timer?.cancel();
          }
          continue;
        }
        await Future.any([changed, stop.future, deadline.future]);
        if (stopped) return await cancel();
        if (deadline.isCompleted) {
          stage = 'TFJOIN180';
          return refuse(
            'The join is unconfirmed. Check Robotopia before retrying; it was left running.',
            3,
          );
        }
        continue;
      }
      write('$stage: ${joinStatusOf(state, null)}');
      final event = await multiplayer.answerJoin(
        state,
        cancellation: token,
        install: install,
        developerMode: developerMode,
        onProgress: take,
      );
      // Always await the gateway: a cancelled install must finish its rollback.
      if (event is JoinProfilePrepared) {
        knownProfileIds.add(state.plan!.derivedProfile.id);
      }
      if (event != null) take(event);
      if (stopped &&
          (event == null ||
              !JoinSession.deferredCancelStages.contains(join!.stage))) {
        return await cancel();
      }
      if (event == null || identical(join, state)) {
        return refuse(
          'This join step could not finish. Check Multiplayer settings and try again.',
        );
      }
    }
  } on Object {
    if (stopped) return await cancel();
    return refuse(
      started == null
          ? 'This join step could not finish. Check Multiplayer settings and try again.'
          : 'The join could not be confirmed. Check Robotopia before retrying; it was left running.',
    );
  } finally {
    finished = true;
    timer?.cancel();
    await subscription?.cancel();
  }
}
