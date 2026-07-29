part of 'widget_test.dart';

abstract class _PublisherFakeLauncherRepository
    implements GameInstallDiscoveryRepository {
  final StreamController<UgcPublisherEvent> _publisherEvents =
      StreamController<UgcPublisherEvent>.broadcast(sync: true);
  bool publisherRunning = false;
  int publisherStartCount = 0;
  int revokePublisherCount = 0;
  int deployFailuresRemaining = 0;
  Object? publisherStartError;
  UgcLiveSyncSettings? deployedUgcSettings;
  bool disposed = false;
  final List<String> selectedGamePaths = [];
  List<GameInstallCandidate>? discoveryOverride;

  @override
  Future<List<GameInstallCandidate>> discoverGameInstalls() async =>
      discoveryOverride ?? (await loadSnapshot()).gameInstallCandidates;

  @override
  Future<GameInstall?> detectKnownInstall() async =>
      (await discoverGameInstalls()).firstOrNull?.install;

  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    selectedGamePaths.add(path);
    final install = (await discoverGameInstalls())
        .firstWhere((candidate) => candidate.install.path == path)
        .install;
    onGameInstallSelected(install);
    return install;
  }

  void onGameInstallSelected(GameInstall install) {}

  @override
  Future<void> dispose() async {
    disposed = true;
    if (!_publisherEvents.isClosed) {
      await _publisherEvents.close();
    }
  }

  @override
  Future<String> deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) async {
    if (deployFailuresRemaining > 0) {
      deployFailuresRemaining -= 1;
      throw StateError('injected UGC deploy failure');
    }
    deployedUgcSettings = settings;
    return '/tmp/topiaforge.ugc.livesync.json';
  }

  @override
  Future<UgcLiveSyncCleanupReport> cleanupUgcLiveSync(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) async => const UgcLiveSyncCleanupReport(
    configPath: '/tmp/topiaforge.ugc.livesync.json',
    commandPath: '/tmp/topiaforge.ugc.livesync.command.json',
  );

  @override
  Stream<UgcPublisherEvent> get ugcPublisherEvents => _publisherEvents.stream;
  @override
  bool get isUgcPublisherRunning => publisherRunning;
  @override
  Future<UgcPublisherStartResult> startUgcPublisher(
    UgcLiveSyncSettings settings,
  ) async {
    publisherStartCount += 1;
    final error = publisherStartError;
    if (error != null) {
      throw error;
    }
    publisherRunning = true;
    return const UgcPublisherStartResult(
      started: true,
      message: 'Publisher started.',
      sessionId: 1,
    );
  }

  @override
  Future<void> stopUgcPublisher({bool waitForExit = false}) async {
    publisherRunning = false;
  }

  @override
  Future<void> revokeUgcPublisherSession() async {
    revokePublisherCount += 1;
  }

  void emitPublisherSession(String documentUrl) {
    emitPublisherPayload('{"documentUrl":"$documentUrl","sceneId":"scene-1"}');
  }

  void emitPublisherPayload(String payload) {
    _publisherEvents.add(
      UgcPublisherOutput(1, 'TOPIAFORGE_UGC_SESSION $payload'),
    );
  }
}

class _SingleInstallOnlyLauncherRepository implements LauncherRepository {
  _SingleInstallOnlyLauncherRepository() : snapshot = _readySnapshot();

  final LauncherSnapshot snapshot;
  String? selectedPath;

  @override
  String get dataRoot => '/tmp/topiaforge-single-install';

  @override
  Stream<UgcPublisherEvent> get ugcPublisherEvents => const Stream.empty();

  @override
  Future<void> dispose() async {}

  @override
  Future<LauncherSnapshot> loadSnapshot() async => snapshot;

  @override
  Future<GameInstall?> detectKnownInstall() async => snapshot.gameInstall;

  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    selectedPath = path;
    return snapshot.gameInstall!;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
