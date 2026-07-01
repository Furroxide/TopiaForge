import 'package:flutter/material.dart';
import 'package:launcher_data/launcher_data.dart';

import 'src/launcher_app.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(
    RobotopiaLauncherApp(
      repository: LocalLauncherRepository(),
      developerRepository: LocalDeveloperRepository(),
    ),
  );
}
