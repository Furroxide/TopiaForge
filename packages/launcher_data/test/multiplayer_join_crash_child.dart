import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'multiplayer_join_content_fixtures.dart';

/// Installs [StandardJoin] into the game at `arguments[0]`, with the
/// launcher data root `arguments[1]` and repository root `arguments[2]`, and
/// dies the way a killed process does when the install reaches the step
/// `arguments[3]`: `exit` runs no `finally` block and no cleanup.
Future<void> main(List<String> arguments) async {
  final dieAt = arguments[3];
  final repository = LocalLauncherRepository(
    dataRoot: arguments[1],
    repositoryRoot: arguments[2],
    packageMetadataValidator: (_) async => const [],
    gameRunningProbe: (_) async => false,
    joinInstallHook: (step) {
      if (step == dieAt) exit(86);
    },
  );
  final install = await repository.selectGameDirectory(arguments[0]);
  final (server, plan) = await StandardJoin.plan();
  final event = await repository.installJoinContent(
    install,
    installingJoin(server, plan),
  );
  stdout.writeln(
    'finished without reaching $dieAt: '
    '${event is JoinFailed ? '${event.code.name}: ${event.detail}' : event}',
  );
  exit(0);
}
