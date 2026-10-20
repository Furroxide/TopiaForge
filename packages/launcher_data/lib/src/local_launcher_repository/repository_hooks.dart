part of '../local_launcher_repository.dart';

typedef PackageInstallCommitHook = FutureOr<void> Function(int committedCount);

/// Runs as a join's install reaches [step]: `staged` once every package is
/// staged, `state` once the manager state is written, `record` once the
/// record of server content is written, and `committed <n>` after the n-th
/// package moves into place. Tests stop or fail an install there.
typedef JoinInstallHook = FutureOr<void> Function(String step);
typedef PackageMetadataValidator =
    Future<List<String>> Function(Directory packageRoot);
typedef RuntimeRepairCommitHook = FutureOr<void> Function(int committedCount);
typedef UgcInspectionReadHook = FutureOr<void> Function(String snapshotPath);
typedef GameProcessStarter = Future<int> Function(GameProcessRequest request);

/// A process creation operation that captures identity from its original OS handle.
typedef GameProcessCreator =
    Future<LaunchProcessReceipt> Function(GameProcessRequest request);
typedef GameProcessIdentityReader =
    Future<LaunchProcessIdentity?> Function(
      int processId,
      String expectedExecutablePath,
    );
typedef GameProcessLiveness =
    Future<bool?> Function(LaunchProcessIdentity identity);
typedef GameProcessStopper =
    Future<bool> Function(LaunchProcessIdentity identity);

/// Reports whether a Robotopia process for this exact install is alive.
///
/// Implementations must fail closed: when liveness cannot be determined they
/// answer true, so an unreadable process list never silently clears a restart
/// requirement the user still needs to see.
typedef GameRunningProbe = Future<bool> Function(GameInstall install);

class GameProcessRequest {
  GameProcessRequest({
    required this.executable,
    required List<String> arguments,
    required this.workingDirectory,
    required Map<String, String> environment,
    this.inheritParentEnvironment = true,
    this.requiredWindowsIdentity,
  }) : arguments = List.unmodifiable(arguments),
       environment = Map.unmodifiable(environment);

  final String executable;
  final List<String> arguments;
  final String workingDirectory;
  final Map<String, String> environment;
  final bool inheritParentEnvironment;
  final WindowsAcceptanceIdentity? requiredWindowsIdentity;
}
