final class SandboxNativePaths {
  const SandboxNativePaths({
    required this.repositoryRoot,
    required this.isolationRecordPath,
    required this.deviceProfilePath,
    required this.packagesPath,
    required this.brokerPath,
    required this.driverManifestPath,
    required this.specPath,
    this.outputRoot = '',
    this.annexPath = '',
    this.sourceWorkspacePath = '',
    this.timeout = const Duration(minutes: 30),
  });
  final String repositoryRoot, isolationRecordPath, deviceProfilePath;
  final String packagesPath, brokerPath, driverManifestPath, specPath;
  final String outputRoot, annexPath, sourceWorkspacePath;
  final Duration timeout;
}
