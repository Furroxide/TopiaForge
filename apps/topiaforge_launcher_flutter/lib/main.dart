import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'src/launcher_app.dart';
import 'src/launcher_exit.dart';
import 'src/launcher_instance.dart';
import 'src/launcher_instance_channel.dart';
import 'src/session_link_inbox.dart';
import 'src/update_health_handshake.dart';

Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();
  final dataRoot = resolveTopiaForgeDataRoot();
  final sessionLinks = SessionLinkInbox();
  final instance = LauncherInstanceChannel(inbox: sessionLinks);
  // One launcher per user and data root: a later start hands its link to the
  // running launcher and exits before it shows anything. An update health
  // check never hands off (see LauncherInstanceGate).
  final role = await LauncherInstanceGate(
    instance,
  ).resolve(arguments, scope: launcherInstanceScope(dataRoot));
  if (role == LauncherInstanceRole.handedOff) {
    await exitLauncher();
    return;
  }
  final startLink = sessionLinkArgument(arguments);
  if (startLink != null) {
    sessionLinks.deliver(
      SessionLinkArrival(startLink, source: SessionLinkSource.commandLine),
    );
  }
  unawaited(instance.ready());

  final repository = LocalLauncherRepository(dataRoot: dataRoot);
  final updateRepository = LocalLauncherUpdateRepository(
    dataRoot: repository.dataRoot,
  );
  scheduleUpdateHealthHandshake(arguments, dataRoot: repository.dataRoot);
  WidgetsBinding.instance.addPostFrameCallback((_) {
    Future<void>.delayed(
      const Duration(seconds: 2),
      updateRepository.recoverPendingUpdate,
    );
  });
  runApp(
    TopiaForgeLauncherApp(
      repository: repository,
      developerRepository: LocalDeveloperRepository(),
      updateRepository: updateRepository,
      // Developer-only NAT reachability probe. Off by default and inert without configured measurement servers:
      // TOPIAFORGE_REACHABILITY_SERVERS takes a comma-separated list of address:port entries. There is no built-in
      // server list on purpose, so a default install contacts nothing. See docs/internal/LauncherReachabilityProbe.md.
      reachabilityProbe: ReachabilityProbeService(
        dataRoot: repository.dataRoot,
        configuredServers:
            (Platform.environment['TOPIAFORGE_REACHABILITY_SERVERS'] ?? '')
                .split(',')
                .map((entry) => entry.trim())
                .where((entry) => entry.isNotEmpty)
                .toList(),
      ),
      sessionLinks: sessionLinks.arrivals,
      // The setting lives in memory, on at every start, until the multiplayer
      // settings store backs it (MultiplayerSettings.linkHandlingEnabled).
      sessionLinkHandling: SessionLinkHandling(
        setting: TransientSessionLinkSetting(),
        registrar: platformSessionLinkRegistrar(),
      ),
    ),
  );
}
