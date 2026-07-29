import 'package:flutter/material.dart';
import 'package:launcher_data/launcher_data.dart';

import 'src/launcher_app.dart';
import 'src/update_health_handshake.dart';

void main(List<String> arguments) {
  WidgetsFlutterBinding.ensureInitialized();
  final repository = LocalLauncherRepository();
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
    ),
  );
}
