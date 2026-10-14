import 'dart:ui' show AppExitType;

import 'package:flutter/services.dart';

/// Quits the launcher through `System.exitApplication`, which every desktop
/// embedder implements. The Windows embedder has no `SystemNavigator.pop`,
/// so that call would leave the window open.
Future<void> exitLauncher() async {
  await ServicesBinding.instance.exitApplication(AppExitType.required);
}
