import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';

/// The facade answers the content stages through its content host, so the
/// reducer's events still come from one place.
final class _RecordingHost implements MultiplayerContentHost {
  final calls = <String>[];
  GameInstall? install;
  CancellationToken? cancellation;

  @override
  Future<JoinEvent?> downloadJoinContent(
    GameInstall install,
    JoinSessionState state, {
    void Function(JoinDownloadProgressed progress)? onProgress,
    CancellationToken? cancellation,
  }) async {
    calls.add('download ${state.stage.name}');
    this.install = install;
    this.cancellation = cancellation;
    onProgress?.call(JoinDownloadProgressed(bytesDone: 1, bytesTotal: 2));
    return const JoinDownloadsCompleted();
  }

  @override
  Future<JoinEvent?> installJoinContent(
    GameInstall install,
    JoinSessionState state, {
    void Function(JoinInstallProgressed progress)? onProgress,
    CancellationToken? cancellation,
  }) async {
    calls.add('install ${state.stage.name}');
    this.install = install;
    this.cancellation = cancellation;
    onProgress?.call(JoinInstallProgressed(packagesDone: 1, packagesTotal: 1));
    return const JoinInstallCompleted();
  }

  @override
  Future<JoinEvent?> prepareJoinProfile(
    GameInstall install,
    JoinSessionState state,
  ) async {
    calls.add('profile ${state.stage.name}');
    this.install = install;
    return const JoinProfilePrepared();
  }

  @override
  Future<ServerContentCleanupPlan> planServerContentCleanup(
    GameInstall install, {
    Set<String>? serverProfileIds,
  }) => throw UnimplementedError();

  @override
  Future<ServerContentCleanupResult> applyServerContentCleanup(
    GameInstall install,
    ServerContentCleanupPlan plan,
  ) => throw UnimplementedError();
}

void main() {
  late Directory root;
  final loopback = InternetAddress.loopbackIPv4;
  const install = GameInstall(
    path: 'game',
    executablePath: 'game/Robotopia.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );

  setUp(() => root = Directory.systemTemp.createTempSync('tf-mp-content-'));
  tearDown(() => root.deleteSync(recursive: true));

  LocalMultiplayerRepository facade({MultiplayerContentHost? content}) {
    final created = LocalMultiplayerRepository(
      dataRoot: root.path,
      service: MultiplayerServiceConfiguration.none,
      lan: LanDiscoveryClient(
        listenAddress: loopback,
        port: 0,
        queryAddress: loopback,
      ),
      diagnostics: MultiplayerNetworkDiagnostics(
        bindAddress: loopback,
        gamePort: 0,
        lanPort: 0,
      ),
      content: content,
    );
    addTearDown(created.dispose);
    return created;
  }

  test('each content stage goes to the content host, and only it', () async {
    final host = _RecordingHost();
    final repository = facade(content: host);
    final archive = TestArchive('example.sandbox', '1.0.0');
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt('https://packages.example/a.zip'),
    });
    final cancellation = CancellationToken();
    final progress = <JoinEvent>[];
    final seen = <String>[];
    var state = consentedJoin(server, plan);
    for (final expected in [
      'download downloading',
      'install installing',
      'profile preparingProfile',
    ]) {
      final event = await repository.answerJoin(
        state,
        cancellation: cancellation,
        install: install,
        onProgress: (update) {
          progress.add(update);
          // Progress events are the reducer's own, taken in its stage.
          state = JoinSession.reduce(state, update);
          seen.add(
            '${state.progress?.bytesDone}/'
            '${state.installProgress?.packagesDone}',
          );
        },
      );
      expect(host.calls.last, expected);
      expect(host.install, same(install));
      state = JoinSession.reduce(state, event!);
    }
    expect(state.stage, JoinStage.launching);
    expect(host.cancellation, same(cancellation));
    expect(progress, [
      isA<JoinDownloadProgressed>(),
      isA<JoinInstallProgressed>(),
    ]);
    expect(seen, ['1/null', 'null/1']);
    expect(state.installProgress, isNull, reason: 'cleared once installed');
    // Stages the launcher's game process or the game answers are not
    // content stages.
    expect(await repository.answerJoin(state, install: install), isNull);
    expect(host.calls, hasLength(3));
  });

  test('a content stage needs the game install', () async {
    final repository = facade(content: _RecordingHost());
    final archive = TestArchive('example.sandbox', '1.0.0');
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt('https://packages.example/a.zip'),
    });
    await expectLater(
      repository.answerJoin(consentedJoin(server, plan)),
      throwsArgumentError,
    );
  });

  test('without a content host the content stages are not answered', () async {
    final repository = facade();
    final archive = TestArchive('example.sandbox', '1.0.0');
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt('https://packages.example/a.zip'),
    });
    expect(
      await repository.answerJoin(
        consentedJoin(server, plan),
        install: install,
      ),
      isNull,
    );
  });

  test('the launcher repository is a content host', () {
    final launcher = LocalLauncherRepository(
      dataRoot: root.path,
      repositoryRoot: root.path,
    );
    addTearDown(launcher.dispose);
    expect(launcher, isA<MultiplayerContentHost>());
    final LauncherRepository asRepository = launcher;
    expect(asRepository is MultiplayerContentHost, isTrue);
  });
}
