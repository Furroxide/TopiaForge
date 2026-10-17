import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'src/launcher_app.dart';
import 'src/launcher_exit.dart';
import 'src/launcher_instance_channel.dart';
import 'src/launcher_startup.dart';
import 'src/session_link_inbox.dart';

Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();
  final dataRoot = resolveTopiaForgeDataRoot();
  final sessionLinks = SessionLinkInbox();
  final instance = LauncherInstanceChannel(inbox: sessionLinks);
  // One launcher per user and data root: a later start hands its link to the
  // running launcher and quits before it shows anything. An update health
  // check never hands off, and no start keeps the data root without a window
  // (see LauncherStartup).
  await LauncherStartup(instance: instance, exit: exitLauncher).run(
    arguments,
    dataRoot: dataRoot,
    scope: launcherInstanceScope(dataRoot),
    launch: (start) {
      final link = start.link;
      if (link != null) {
        sessionLinks.deliver(
          SessionLinkArrival(link, source: SessionLinkSource.commandLine),
        );
      }
      unawaited(instance.ready());
      _runLauncher(dataRoot, start, sessionLinks);
    },
  );
}

void _runLauncher(
  String dataRoot,
  LauncherStart start,
  SessionLinkInbox sessionLinks,
) {
  final repository = LocalLauncherRepository(dataRoot: dataRoot);
  final updateRepository = LocalLauncherUpdateRepository(
    dataRoot: repository.dataRoot,
  );
  start.healthCheck?.schedule();
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
      // settings store backs it (MultiplayerSettings.linkHandlingEnabled). A
      // launcher on its own data root leaves the system registration alone.
      sessionLinkHandling: SessionLinkHandling(
        setting: TransientSessionLinkSetting(),
        registrar: platformSessionLinkRegistrar(dataRoot: dataRoot),
      ),
    ),
  );
}
