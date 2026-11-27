import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';
import 'multiplayer_network_support.dart';

/// A game that records what the launcher staged as it started, the way the
/// manager reads it: the launch request, and the copy of the preferences it
/// reads at start. Like the game, it deletes the request once it read it.
final class _RecordingGame {
  final requests = <String>[];
  final projections = <String?>[];

  Future<LaunchProcessReceipt> start(
    GameProcessRequest request,
    Directory game,
  ) async {
    final file = File(
      request.environment[ProfileLaunchConfigurationV4.environmentVariable]!,
    );
    requests.add(file.readAsStringSync());
    file.deleteSync();
    final projection = File(
      p.join(managerStaging(game).path, 'multiplayer-preferences-v1.json'),
    );
    projections.add(
      projection.existsSync() ? projection.readAsStringSync() : null,
    );
    final identity = LaunchProcessIdentity(
      pid: 4200 + requests.length,
      executablePath: p.join(game.path, 'Robotopia.exe'),
      startTimeUtc: DateTime.utc(2026, 10, 25),
      nativeStartToken: 'generation:${requests.length}',
    );
    return LaunchProcessReceipt(pid: identity.pid, identity: identity);
  }
}

/// A rig whose game starts as [game] records, with the player's mods.
Future<JoinRig> _launchRig(
  _RecordingGame game, {
  GameRunningProbe? probe,
}) async {
  late final JoinRig rig;
  rig = await JoinRig.create(
    withRuntime: true,
    probe: probe,
    creator: (request) => game.start(request, rig.game),
    liveness: (_) async => true,
  );
  await preparePlayer(rig);
  return rig;
}

LocalMultiplayerRepository _facade(
  JoinRig rig, {
  MultiplayerServiceConfiguration service =
      MultiplayerServiceConfiguration.none,
}) {
  final multiplayer = LocalMultiplayerRepository(
    dataRoot: rig.dataRoot,
    service: service,
    controlPlane: const UnavailableControlPlane(),
    content: rig.repository,
    gameProbe: (_) async => false,
  );
  addTearDown(multiplayer.dispose);
  return multiplayer;
}

/// The player's own Alpha 1.0.0 and Gamma 1.0.0, installed, as a server
/// runs them, and a consented join of it: of a direct link to a host on the
/// local network, or of a LAN listing.
Future<(VerifiedServerDescriptor, JoinSessionState)> _installedJoin({
  bool lanListing = false,
  bool closeOwnedGame = false,
  List<TestArchive>? archives,
}) async {
  final runs = archives ?? [StandardJoin.alpha, StandardJoin.gamma];
  final server = await verifiedServer([
    for (final archive in runs) archive.package,
  ]);
  final plan = planJoin(server, {
    for (final archive in runs)
      archive.sha256Hex: JoinPackageResolution.installed(archive.manifest),
  });
  if (!lanListing) {
    return (
      server,
      consentedJoin(
        server,
        plan,
        onLocalNetwork: true,
        closeOwnedGame: closeOwnedGame,
      ),
    );
  }
  final join =
      [
        JoinDescriptorVerified(server),
        JoinPlanReady(plan),
        JoinConsentGiven(closeOwnedGameAcknowledged: closeOwnedGame),
      ].fold(
        JoinSession.start(
          JoinListingTarget(
            serverId: server.serverId,
            source: ServerListingSource.lan,
          ),
          privacy: SessionPrivacy.relayOnly,
        ),
        JoinSession.reduce,
      );
  return (server, join);
}

/// [join] taken through its content stages to launching.
Future<JoinSessionState> _toLaunching(
  JoinRig rig,
  LocalMultiplayerRepository multiplayer,
  JoinSessionState join,
) async {
  var state = join;
  while (state.stage != JoinStage.launching && !state.isEnded) {
    final event = await multiplayer.answerJoin(state, install: rig.install);
    expect(event, isNotNull, reason: state.stage.name);
    state = JoinSession.reduce(state, event!);
  }
  expect(
    state.stage,
    JoinStage.launching,
    reason: '${state.outcome?.failure?.detail}',
  );
  return state;
}

void main() {
  test('a join writes the game\'s copy of the preferences, then starts the '
      'server profile with request V5 as the fixtures write it', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    await multiplayer.changePreferences(
      const MultiplayerPreferencesChange(displayName: 'Sam'),
    );
    final (server, consented) = await _installedJoin();
    final launching = await _toLaunching(rig, multiplayer, consented);

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-test',
    );

    expect(launch, isA<JoinLaunchStarted>());
    launch as JoinLaunchStarted;
    expect(launch.live, isFalse);
    expect(game.requests, hasLength(1));
    // The game found its copy of the preferences when it started.
    expect(game.projections.single, isNotNull);
    expect(
      LaunchTransport.readPreferences(game.projections.single!).displayName,
      'Sam',
    );
    final text = game.requests.single;
    final request = LaunchTransport.readProfileV5(text);
    expect(LaunchTransport.writeProfileV5(request), text);
    expect(StagedLaunchRequest.decode(text).schemaVersion, 5);
    final session = request.session;
    final target = session.target! as LaunchDirectTarget;
    expect(
      {
        'command': request.command,
        'role': session.role.name,
        'executableCommand': request.executable.command,
        'intentId': session.intentId,
        'displayName': session.displayName,
        'developerMode': session.developerMode,
        'privacy': session.transport.privacy.wireName,
        'consented': session.transport.directConsent != null,
        'port': session.transport.port,
        'serviceOrigin': session.serviceOrigin,
        'targetKind': target.kind,
        'packageCount': request.packages.length,
      },
      {
        'command': 'join-session',
        'role': 'join',
        'executableCommand': 'main-menu',
        'intentId': 'join-test',
        'displayName': 'Sam',
        'developerMode': false,
        'privacy': 'relay-only',
        'consented': false,
        'port': 0,
        'serviceOrigin': null,
        'targetKind': 'direct',
        'packageCount': 2,
      },
    );
    expect(target.serverId, server.serverId);
    expect(target.descriptorSha256, server.descriptorSha256);
    expect(target.packageDigest, server.packageDigest);
    expect(request.inheritManagerModState, isFalse);
    expect(request.profileId, JoinPlanner.derivedProfileId(server.serverId));
    // Member for member what the shared fixture's direct join holds.
    final fixture = asObjects(
      readMultiplayerFixture('launch-v5/cases.json')['valid'],
    ).singleWhere((item) => item['id'] == 'join-direct-lan');
    final document = asObject(jsonDecode(text));
    final expected = asObject(fixture['document']);
    expect(document.keys.toSet(), expected.keys.toSet());
    expect(
      asObject(document['session']).keys.toSet(),
      asObject(expected['session']).keys.toSet(),
    );
    expect(
      asObject(asObject(document['session'])['target']).keys.toSet(),
      asObject(asObject(expected['session'])['target']).keys.toSet(),
    );
  });

  test('the address a join names stays in its launch request: not in the '
      'launcher log, a diagnostic bundle, or anything left on disk', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    final (_, consented) = await _installedJoin();
    final launching = await _toLaunching(rig, multiplayer, consented);
    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-private',
    );
    expect(launch, isA<JoinLaunchStarted>());
    expect('$launch', isNot(contains('192.168')));
    expect(game.requests.single, contains('192.168.1.20'));

    final bundle = await rig.repository.createDiagnosticBundle(
      rig.install,
      const DependencyResolutionResult(orderedMods: [], issues: [], graph: {}),
    );
    final archive = ZipDecoder().decodeBytes(
      File(bundle.path).readAsBytesSync(),
    );
    for (final entry in archive.files) {
      expect(
        utf8.decode(entry.content as List<int>, allowMalformed: true),
        isNot(contains('192.168.1.20')),
        reason: entry.name,
      );
    }
    for (final file in rig.root.listSync(recursive: true).whereType<File>()) {
      if (file.path.endsWith('.zip')) continue;
      expect(
        utf8.decode(file.readAsBytesSync(), allowMalformed: true),
        isNot(contains('192.168.1.20')),
        reason: p.relative(file.path, from: rig.root.path),
      );
    }
  });

  test('a game the launcher did not start ends the join before anything '
      'starts', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game, probe: (_) async => true);
    final multiplayer = _facade(rig);
    final (_, consented) = await _installedJoin(closeOwnedGame: true);
    final launching = await _toLaunching(rig, multiplayer, consented);
    expect(
      await multiplayer.joinGameRunning(rig.install),
      JoinGameRunning.foreign,
    );

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-foreign',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.gameRunning);
    expect(failure.gameOwnership, JoinGameOwnership.foreign);
    expect(game.requests, isEmpty);
    expect(JoinSession.reduce(launching, failure).isEnded, isTrue);
  });

  test('a start that would open in safe mode is never made', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    final (_, consented) = await _installedJoin();
    final launching = await _toLaunching(rig, multiplayer, consented);
    // The last run never recorded a clean exit, as after a crash.
    File(p.join(rig.game.path, 'BepInEx', 'TopiaForge', 'startup-journal.json'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync(
        jsonEncode({
          'currentModId': '',
          'schemaVersion': 1,
          'sessionId': '0f1e2d3c4b5a69788796a5b4c3d2e1f0',
          'startedAtUtc': '2026-10-25T00:00:00.0000000Z',
          'state': 'startup-complete',
          'updatedAtUtc': '2026-10-25T00:00:01.0000000Z',
        }),
      );

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-safe-mode',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.launchBlocked);
    expect(failure.detail, contains('safe mode'));
    expect(failure.detail, contains('quit it normally, then join again'));
    expect(game.requests, isEmpty);
  });

  test('a copy of the preferences that cannot be written stops the start, '
      'so the game never starts with older choices', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    final (_, consented) = await _installedJoin();
    final launching = await _toLaunching(rig, multiplayer, consented);
    // Something else holds the copy's place: a folder named like the file.
    Directory(
      p.join(managerStaging(rig.game).path, 'multiplayer-preferences-v1.json'),
    ).createSync(recursive: true);

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-no-copy',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.launchBlocked);
    expect(failure.detail, contains("copy of your multiplayer settings"));
    expect(game.requests, isEmpty);
  });

  test('the request takes the privacy chosen for the session: direct play '
      'carries its consent for this intent', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    await multiplayer.changePreferences(
      const MultiplayerPreferencesChange(acknowledgeDisclosure: true),
    );
    final settings = (await multiplayer.loadPreferences()).settings;
    final server = await verifiedServer([StandardJoin.alpha.package]);
    final plan = planJoin(server, {
      StandardJoin.alpha.sha256Hex: JoinPackageResolution.installed(
        StandardJoin.alpha.manifest,
      ),
    });
    final consented =
        [
          JoinDescriptorVerified(server),
          JoinPlanReady(plan),
          const JoinConsentGiven(),
        ].fold(
          JoinSession.start(
            JoinLinkTarget(
              SessionLink.direct(
                serverId: server.serverId,
                host: '203.0.113.7',
                port: 27250,
              ),
            ),
            privacy: settings.privacyForSession(allowDirect: true),
          ),
          JoinSession.reduce,
        );
    final launching = await _toLaunching(rig, multiplayer, consented);

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-direct',
    );

    expect(launch, isA<JoinLaunchStarted>());
    final transport = LaunchTransport.readProfileV5(
      game.requests.single,
    ).session.transport;
    expect(transport.privacy, MultiplayerAddressPrivacy.direct);
    expect(transport.directConsent!.intentId, 'join-direct');
    expect(transport.directConsent!.disclosure, 1);
  });

  test('a join the game could not reach is refused with what to do', () async {
    final game = _RecordingGame();
    final rig = await _launchRig(game);
    final multiplayer = _facade(rig);
    final (_, consented) = await _installedJoin(lanListing: true);
    final launching = await _toLaunching(rig, multiplayer, consented);
    await multiplayer.changePreferences(
      const MultiplayerPreferencesChange(lanDiscovery: false),
    );

    final launch = await multiplayer.launchJoin(
      launching,
      install: rig.install,
      intentId: 'join-lan-off',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.launchBlocked);
    expect(failure.detail, contains('Turn LAN discovery on'));
    expect(game.requests, isEmpty);
  });

  group('planning', () {
    test('plans from what the install holds, for its game build', () async {
      final rig = await JoinRig.create();
      await preparePlayer(rig);
      final (server, _) = await StandardJoin.plan();
      final checking =
          [
            JoinTargetResolved(server.serverId),
            JoinDescriptorVerified(server),
          ].fold(
            JoinSession.start(
              JoinLinkTarget(SessionLink.server(server.serverId)),
              privacy: SessionPrivacy.relayOnly,
            ),
            JoinSession.reduce,
          );
      expect(checking.stage, JoinStage.checking);

      final unknownBuild = await rig.repository.planJoinContent(
        rig.install.copyWith(clearGameVersion: true),
        checking,
      );
      expect(
        (unknownBuild! as JoinFailed).code,
        JoinFailureCode.gameBuildMismatch,
      );

      final answer = await rig.repository.planJoinContent(
        rig.install.copyWith(gameVersion: '0.0.2545'),
        checking,
      );
      final plan = (answer! as JoinPlanReady).plan;
      expect(plan.server.serverId, server.serverId);
      final gamma = plan.packages.singleWhere(
        (item) => item.package.id == 'own.gamma',
      );
      expect(gamma.disposition, JoinPackageDisposition.useInstalled);
      // Nothing in the player's sources offers the server's versions.
      expect(plan.isJoinable, isFalse);
      expect(plan.blockers.map((blocker) => blocker.code).toSet(), {
        JoinBlockerCode.contentUnavailable,
      });
      // Only a join that is checking is planned.
      final cancelled = JoinSession.reduce(
        checking,
        const JoinCancelRequested(),
      );
      expect(
        await rig.repository.planJoinContent(rig.install, cancelled),
        isNull,
      );
    });
  });
}
