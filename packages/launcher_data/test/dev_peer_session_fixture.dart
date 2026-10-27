import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

/// Only installation is simulated. Sessions read real staging files and own
/// real inert VM processes through the production Windows creation/stop APIs.
final class PeerSessionFixture extends LocalLauncherRepository {
  PeerSessionFixture(this.root, this.index)
    : super(
        dataRoot: p.join(root.path, 'launcher-$index'),
        repositoryRoot: root.path,
      );

  final Directory root;
  final int index;
  bool omitIdentity = false;
  LaunchProcessReceipt? receipt;
  LauncherProfile? savedProfile;
  LaunchSessionRequest? sessionRequest;
  String get requestId => 'request-$index';
  String get gameRoot => p.join(root.path, 'peer $index');
  File get report => File(p.join(gameRoot, 'ready.json'));
  File get stop => File(p.join(gameRoot, 'stop'));
  DevPeerOverlay get overlay =>
      DevPeerOverlay(index, gameRoot, p.join(root.path, 'launcher-$index'));
  GameInstall get install => GameInstall(
    path: gameRoot,
    executablePath: vm,
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  static String get vm =>
      p.join(p.dirname(Platform.resolvedExecutable), 'dartvm.exe');

  @override
  Future<GameInstall?> detectKnownInstall() async {
    Directory(gameRoot).createSync(recursive: true);
    return install;
  }

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) async =>
      const RepairReport(actions: [], issues: []);

  @override
  Future<LauncherSnapshot> loadSnapshot() async => const LauncherSnapshot(
    profiles: [],
    selectedProfileId: '',
    installedMods: [],
    registryMods: [],
    packageSources: [],
    recentLog: '',
  );

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    savedProfile = profiles.single;
    return profiles;
  }

  @override
  Future<LaunchResult> launchSession(
    GameInstall install,
    LauncherProfile profile,
    LaunchSessionRequest request, {
    LaunchSelection? selectionOverride,
    bool restart = false,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  }) async {
    sessionRequest = request;
    final script = File(p.join(gameRoot, 'inert peer.dart'));
    script.writeAsStringSync(r'''
import 'dart:async'; import 'dart:convert'; import 'dart:io';
Future<void> main(List<String> args) async {
  File('${args[0]}.tmp').writeAsStringSync(jsonEncode({'pid': pid, 'cwd': Directory.current.path,
    'arguments': args.skip(2).toList(), 'root': Platform.environment['TOPIAFORGE_DATA_ROOT']}));
  File('${args[0]}.tmp').renameSync(args[0]);
  final deadline = Stopwatch()..start();
  while (!File(args[1]).existsSync() && deadline.elapsed < const Duration(seconds: 45)) {
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
}
''');
    receipt = await startLaunchProcessWithReceipt(
      executable: vm,
      arguments: [
        script.path,
        report.path,
        stop.path,
        ...profile.launchSettings.extraArguments,
      ],
      workingDirectory: gameRoot,
      environment: profile.launchSettings.environment,
    );
    final elapsed = Stopwatch()..start();
    while (!report.existsSync()) {
      if (elapsed.elapsed > const Duration(seconds: 10)) {
        throw TimeoutException('Inert peer did not start.');
      }
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
    return LaunchResult(
      started: true,
      message: 'Inert fixture started.',
      requestId: requestId,
      processId: receipt!.pid,
      process: omitIdentity ? null : receipt!.identity,
    );
  }

  void channel(String prefix, Map<String, Object?> value) {
    final directory = Directory(
      p.join(gameRoot, 'BepInEx', 'TopiaForge', 'staging'),
    )..createSync(recursive: true);
    File(
      p.join(
        directory.path,
        '$prefix-${LaunchStorageKeys.request(requestId)}.json',
      ),
    ).writeAsStringSync(jsonEncode(value));
  }

  void ready() => channel(
    'launch-progress',
    LaunchProgress(
      requestId: requestId,
      sequence: 1,
      phase: 'running',
      session: LaunchProgressSession(
        role: LaunchSessionRole.host,
        intentId: 'fixture-$index',
        state: LaunchSessionState.hosting,
      ),
    ).toJson(),
  );

  Future<void> retire() async {
    final identity = receipt?.identity;
    if (identity != null && await isLaunchProcessAlive(identity) != false) {
      await stopLaunchProcess(identity);
    }
    if (identity != null && await isLaunchProcessAlive(identity) != false) {
      throw StateError('Retain fixture until its owned process exits.');
    }
    await dispose();
  }
}
