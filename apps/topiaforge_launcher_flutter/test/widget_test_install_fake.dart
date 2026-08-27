part of 'widget_test.dart';

abstract class _InstallFakeLauncherRepository
    implements GameInstallDiscoveryRepository {
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
  }
}

class _SingleInstallOnlyLauncherRepository implements LauncherRepository {
  _SingleInstallOnlyLauncherRepository() : snapshot = _readySnapshot();

  final LauncherSnapshot snapshot;
  String? selectedPath;

  @override
  String get dataRoot => '/tmp/topiaforge-single-install';

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
