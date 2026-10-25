import 'dart:async';
import 'dart:math';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'cli_host_monitor.dart';
import 'cli_host_options.dart';
import 'cli_multiplayer_text.dart';

Future<int> runCliHost({
  required LauncherRepository launcher,
  required MultiplayerRepository multiplayer,
  required GameInstall install,
  required LauncherProfile profile,
  required bool developerMode,
  required CliHostOptions options,
  required bool hasTerminal,
  required Future<bool> Function(String prompt) confirm,
  required void Function(String) write,
  required Future<LaunchResult> Function(LaunchSessionRequest) launch,
  String? serviceOrigin,
  Future<void>? cancelled,
  String Function()? newIntent,
}) async {
  var stage = 'TFHOST110';
  var stopped = false;
  CliHostMonitor? monitor;
  StreamSubscription<LaunchActivity>? subscription;
  Timer? timeout;
  final token = CancellationToken();
  if (cancelled != null) {
    unawaited(
      cancelled.then((_) {
        stopped = true;
        token.cancel();
        monitor?.cancel();
      }),
    );
  }
  int refuse(String message, [int code = 1]) {
    write('$stage: $message');
    return code;
  }

  Future<bool> consent(String prompt) async =>
      !stopped && (hasTerminal ? await confirm(prompt) : options.yes);

  try {
    if (!hasTerminal && !options.yes) {
      return refuse(
        'Hosting needs consent. Read the host options and rerun with --yes when there is no terminal.',
        2,
      );
    }
    if (options.servePackages && !developerMode) {
      return refuse(
        'Serving packages requires developer mode. Enable it in the launcher or omit --serve-packages.',
        2,
      );
    }
    if (await multiplayer.gameRunState(install) != GameRunState.notRunning) {
      return refuse(
        'Robotopia is running or its state is unknown. Close the game and try again.',
      );
    }
    final round = await multiplayer.syncWithGame(install, cancellation: token);
    if (stopped) return 130;
    if (round is! MultiplayerPreferencesSynced || !round.preferences.readable) {
      return refuse(
        'Multiplayer preferences could not be synchronized. Close programs using the data folder and retry.',
      );
    }
    final preferences = round.preferences;
    var settings = preferences.settings;
    final defaults = preferences.hosting;
    final visibility = options.visibility ?? defaults.visibility;
    final needsService =
        visibility == ServerVisibility.public ||
        visibility == ServerVisibility.invite;
    stage = 'TFHOST120';
    if (needsService) {
      if (serviceOrigin == null) {
        return refuse(
          'This build has no multiplayer service. Use --visibility lan or a build configured for Internet play.',
        );
      }
      final status = await multiplayer.readServiceStatus(cancellation: token);
      if (stopped) return 130;
      final features = status.features;
      if (features == null ||
          !features.hosting ||
          !features.signaling ||
          !features.invites) {
        return refuse(
          'Internet hosting or invites are unavailable. Retry later or use --visibility lan.',
        );
      }
      if (!features.relayAvailable && !options.allowDirect) {
        return refuse(
          'The relay is unavailable. Keep Hide my IP on with --visibility lan, or explicitly choose --allow-direct for this session.',
        );
      }
    }
    if ((visibility == ServerVisibility.lan ||
            visibility == ServerVisibility.public) &&
        !settings.lanDiscoveryEnabled) {
      return refuse(
        'LAN discovery is off. Enable it in Multiplayer settings or choose private or invite visibility.',
      );
    }
    if ((options.port ?? defaults.port) == LanBeacon.discoveryPort) {
      return refuse(
        'The saved hosting port is reserved for LAN discovery. Choose another port with --port.',
      );
    }
    final preview = await launcher.previewLaunch(
      install,
      profile,
      selectionOverride: options.launch.selectionOverride,
    );
    if (stopped) return 130;
    if (!preview.canLaunch ||
        profile.launchSettings.safeMode ||
        preview.profileId != profile.id ||
        preview.profileRevision != profile.revision) {
      return refuse(
        'The selected profile cannot host. Repair its runtime and launch selection in Setup, turn Safe Mode off, then retry.',
      );
    }
    stage = 'TFHOST110';
    if (settings.needsPrivacyDisclosure) {
      CliMultiplayerText.disclosure.forEach(write);
      if (!await consent('I understand [y/N]')) {
        return refuse('Hosting cancelled. Run host again when ready.', 130);
      }
      if (await multiplayer.gameRunState(install) != GameRunState.notRunning) {
        return refuse(
          'Robotopia started while consent was open. Close it and try again.',
        );
      }
      final saved = await multiplayer.changePreferences(
        const MultiplayerPreferencesChange(acknowledgeDisclosure: true),
      );
      settings = saved.settings;
    }
    if (options.allowDirect) {
      write('Allow a direct connection?');
      write(CliMultiplayerText.direct);
      if (!await consent('Allow for this session [y/N]')) {
        return refuse(
          'Direct hosting cancelled. Omit --allow-direct to keep Hide my IP on.',
          130,
        );
      }
    }
    final voice = defaults.voice && settings.voiceActive;
    write(voice ? CliMultiplayerText.voiceOn : CliMultiplayerText.voiceOff);
    write('Host visibility: ${visibility.wireName}.');
    if (!await consent('Start hosting [y/N]')) {
      return refuse('Hosting cancelled. Run host again when ready.', 130);
    }
    if (stopped) return 130;
    if (await multiplayer.gameRunState(install) != GameRunState.notRunning) {
      return refuse(
        'Robotopia started while consent was open. Close it and try again.',
      );
    }
    final synced = await multiplayer.syncWithGame(install, cancellation: token);
    if (stopped) return 130;
    // A second writer must never change the preferences under this consent.
    if (synced is! MultiplayerPreferencesSynced ||
        synced.preferences.settings.toJson().toString() !=
            settings.toJson().toString() ||
        synced.preferences.hosting.toJson().toString() !=
            defaults.toJson().toString()) {
      return refuse(
        'Multiplayer preferences changed or could not be saved. Run host again to review the current choices.',
      );
    }
    final intent =
        newIntent?.call() ??
        List.generate(
          16,
          (_) => Random.secure().nextInt(256).toRadixString(16).padLeft(2, '0'),
        ).join();
    final request = LaunchSessionRequest.host(
      intentId: intent,
      settings: settings,
      privacy: settings.privacyForSession(allowDirect: options.allowDirect),
      host: LaunchHostOptions(
        name: options.name ?? defaults.name,
        maxPlayers: options.maxPlayers ?? defaults.maxPlayers,
        visibility: visibility,
        voice: voice,
        passwordRequired: options.password,
        servePackages: options.servePackages,
      ),
      port: options.port ?? defaults.port,
      serviceOrigin: serviceOrigin,
      developerMode: developerMode,
    );
    stage = 'TFHOST130';
    monitor = CliHostMonitor(profile, intent, needsService, write);
    subscription = launcher.launchActivities.listen(
      monitor.accept,
      onError: (Object _) => monitor?.unavailable(),
      onDone: () => monitor?.unavailable(),
    );
    final result = await launch(request);
    if (!result.processStarted) {
      return refuse(
        'The game could not start hosting. Close any running copy, repair the selected profile, and retry.',
      );
    }
    write('TFHOST130: Game started; waiting for hosting confirmation.');
    monitor.bind(result);
    if (stopped) monitor.cancel();
    timeout = Timer(
      options.launch.waitTimeout,
      () => monitor?.unavailable(force: true),
    );
    return await monitor.completed.future;
  } on Object {
    if (stopped) return 130;
    return refuse(
      'Hosting could not finish this step. Check Multiplayer settings and the selected profile, then retry.',
    );
  } finally {
    timeout?.cancel();
    monitor?.close();
    await subscription?.cancel();
  }
}
