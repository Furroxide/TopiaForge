import 'package:launcher_domain/launcher_domain.dart';

/// The Robotopia build this checkout pins. `topiaforge compat bump` rewrites
/// the runtime constant it derives from, so the Sandbox contracts follow a
/// retarget without a literal of their own.
final int sandboxGameBuild = RobotopiaGameVersion.tryBuildId(
  TopiaForgeRuntimeVersions.gameVersion,
)!;
