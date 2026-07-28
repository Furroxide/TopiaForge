part of 'widget_test.dart';

class _FakeLauncherUpdateRepository implements LauncherUpdateRepository {
  _FakeLauncherUpdateRepository({
    this.checkStatus = const LauncherUpdateStatus(
      phase: LauncherUpdatePhase.current,
      message: 'TopiaForge is up to date.',
    ),
  });

  final StreamController<LauncherUpdateStatus> _statuses =
      StreamController<LauncherUpdateStatus>.broadcast();
  final LauncherUpdateStatus checkStatus;
  int checkCount = 0;
  int stageCount = 0;
  int applyCount = 0;

  void emit(LauncherUpdateStatus status) => _statuses.add(status);

  @override
  Stream<LauncherUpdateStatus> get statuses => _statuses.stream;

  @override
  Future<LauncherUpdateStatus> checkForUpdate({
    required String currentVersion,
    required LauncherUpdateChannel channel,
    bool force = false,
  }) async {
    checkCount += 1;
    return checkStatus;
  }

  @override
  Future<LauncherUpdateStatus> stageUpdate(
    LauncherUpdateCandidate candidate,
  ) async {
    stageCount += 1;
    return LauncherUpdateStatus(
      phase: LauncherUpdatePhase.staged,
      candidate: candidate,
      progress: 1,
      stagedPlanPath: 'fixture-plan.json',
      message: 'The complete signed package is staged.',
    );
  }

  @override
  Future<void> applyStagedUpdate(LauncherUpdateStatus staged) async {
    applyCount += 1;
  }

  @override
  Future<void> recoverPendingUpdate() async {}

  @override
  Future<void> dispose() => _statuses.close();
}

LauncherUpdateCandidate _updateCandidate() {
  const version = '1.0.0-rc.2';
  const hash =
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  return LauncherUpdateCandidate(
    version: version,
    tag: 'v$version',
    channel: LauncherUpdateChannel.beta,
    minimumUpdaterVersion: '1.0.0-rc.1',
    releaseUrl:
        'https://github.com/furroxide/TopiaForge/releases/tag/v$version',
    signingKeyId: 'ed25519:0123456789abcdef',
    payloadSha256: hash,
    platforms: {
      for (final entry in const {
        'windows-x64': (
          name: 'TopiaForge-windows-x64.zip',
          layout: 'portable-root',
        ),
        'linux-x64': (
          name: 'TopiaForge-linux-x64.zip',
          layout: 'portable-root',
        ),
        'macos-universal': (
          name: 'TopiaForge-macos-universal.zip',
          layout: 'app-bundle',
        ),
      }.entries)
        entry.key: LauncherUpdateArtifact(
          platform: entry.key,
          assetName: entry.value.name,
          url:
              'https://github.com/furroxide/TopiaForge/releases/download/'
              'v$version/${entry.value.name}',
          sha256: hash,
          size: 1024,
          entryCount: 2,
          expandedSize: 2048,
          installLayout: entry.value.layout,
        ),
    },
  );
}
