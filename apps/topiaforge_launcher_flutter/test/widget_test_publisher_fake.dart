part of 'widget_test.dart';

abstract class _PublisherFakeLauncherRepository implements LauncherRepository {
  final StreamController<UgcPublisherEvent> _publisherEvents =
      StreamController<UgcPublisherEvent>.broadcast(sync: true);
  bool publisherRunning = false;
  int publisherStartCount = 0;
  int revokePublisherCount = 0;
  int deployFailuresRemaining = 0;
  Object? publisherStartError;
  UgcLiveSyncSettings? deployedUgcSettings;
  bool disposed = false;

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
