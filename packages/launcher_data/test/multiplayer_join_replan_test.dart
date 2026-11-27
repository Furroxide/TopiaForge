import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// A join plans from what will stay installed. What a join that failed left
/// in place, for a later start to take out, never counts as installed, and an
/// install checks again that what its plan found installed is still there,
/// so no server profile is saved pinning a version that is gone or going.
void main() {
  test('a join right after one whose rollback was held installs the held '
      'version afresh once the hold ends', () async {
    final (rig, hold) = await _failedJoinHoldingAlpha();
    final server = await _standardServer();
    final before = await rig.playerView();
    final (first, _) = await _join(rig, server);
    final release = hold.release;
    if (release == null) return; // Skipped: this account moves any file.
    expect(first.outcome?.failure?.rollbackComplete, isFalse);
    expect(packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(), isTrue);
    await release();

    // The player joins again at once, with no start in between. The held
    // folder goes at this install's own sweep, so its plan downloads it.
    final (second, plan) = await _join(rig, server);
    expect(_dispositionOf(plan, 'own.alpha'), JoinPackageDisposition.download);
    expect(
      second.stage,
      JoinStage.launching,
      reason: '${second.outcome?.failure?.detail}',
    );
    final serverProfile = (await rig.repository.loadSnapshot()).profiles
        .singleWhere(ServerProfiles.isServerProfile);
    expect(await rig.loadsOf(serverProfile), [
      'own.alpha@2.0.0',
      'own.beta@1.5.0',
      'own.gamma@1.0.0',
      'server.delta@1.0.0',
    ]);
    expect(await _notInstalledPins(rig, serverProfile), isEmpty);
    expect((await rig.playerView()).loads, before.loads);
    expect(joinFolders(rig.game), isEmpty);
  });

  test('a join that would download nothing is refused while an earlier join '
      'cannot be put back yet', () async {
    final (rig, hold) = await _failedJoinHoldingAlpha();
    final (first, _) = await _join(rig, await _standardServer());
    final release = hold.release;
    if (release == null) return; // Skipped: this account moves any file.
    expect(first.outcome?.failure?.rollbackComplete, isFalse);

    // Another server runs that Alpha 2.0.0, the same archive, and the
    // player's Gamma: were the held folder counted as installed, the join
    // would download nothing, never reach the install, and save a server
    // profile on a folder the next start takes out.
    final other = await verifiedServer(
      [StandardJoin.serverAlpha.package, StandardJoin.gamma.package],
      name: 'Other Server',
      signingKey: List<int>.filled(32, 7),
    );
    final (refused, plan) = await _join(rig, other);
    expect(_dispositionOf(plan, 'own.alpha'), JoinPackageDisposition.download);
    expect(refused.outcome?.failure?.code, JoinFailureCode.installFailed);
    expect(refused.outcome?.failure?.detail, contains('still being put back'));
    expect(rig.storedProfiles(servers: true), isEmpty);

    await release();
    rig.repository = rig.open();
    await rig.repository.loadSnapshot();
    final (joined, _) = await _join(rig, other);
    expect(
      joined.stage,
      JoinStage.launching,
      reason: '${joined.outcome?.failure?.detail}',
    );
    final serverProfile = (await rig.repository.loadSnapshot()).profiles
        .singleWhere(ServerProfiles.isServerProfile);
    expect(await rig.loadsOf(serverProfile), [
      'own.alpha@2.0.0',
      'own.gamma@1.0.0',
    ]);
    expect(await _notInstalledPins(rig, serverProfile), isEmpty);
  });

  for (final change in ['uninstalled', 'installed again as another build']) {
    test('an install refuses when a version its plan found installed was '
        '$change since', () async {
      final rig = await JoinRig.create();
      await preparePlayer(rig);
      await _offer(rig);
      final server = await _standardServer();
      final plan = await _plan(rig, server);
      expect(
        _dispositionOf(plan, 'own.gamma'),
        JoinPackageDisposition.useInstalled,
      );
      // Gamma changes while the consent sheet is open, through Mods.
      await rig.repository.uninstallMod(rig.install, 'own.gamma');
      expect(
        packageFolder(rig.game, 'own.gamma', '1.0.0').existsSync(),
        isFalse,
      );
      if (change != 'uninstalled') {
        await rig.installOwn(
          TestArchive('own.gamma', '1.0.0', name: 'Gamma', payload: 'other'),
        );
        expect(
          packageFolder(rig.game, 'own.gamma', '1.0.0').existsSync(),
          isTrue,
        );
      }
      rig.cache(StandardJoin.downloads);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(failure.detail, contains('changed while the join was preparing'));
      expect(failure.detail, contains('Join again.'));
      for (final archive in StandardJoin.downloads) {
        expect(
          packageFolder(rig.game, archive.id, archive.version).existsSync(),
          isFalse,
        );
      }
      expect(serverContentFile(rig.game).existsSync(), isFalse);
      expect(joinFolders(rig.game), isEmpty);
    });
  }
}

/// The release of a hold the first join's hook takes; null until it is
/// taken, and when this account moves any file anyway.
final class _Hold {
  Future<void> Function()? release;
}

/// A rig whose first join fails at its second commit while something holds
/// the server Alpha's assembly, as an antivirus scan of a file just moved
/// into place can, so its rollback cannot take that folder out.
Future<(JoinRig, _Hold)> _failedJoinHoldingAlpha() async {
  late JoinRig rig;
  final hold = _Hold();
  var held = false;
  rig = await JoinRig.create(
    hook: (step) async {
      if (step != 'committed 2' || held) return;
      held = true;
      hold.release = await holdInPlace(
        File(
          p.join(
            packageFolder(rig.game, 'own.alpha', '2.0.0').path,
            'OwnAlpha.dll',
          ),
        ),
      );
      throw StateError('Stopped.');
    },
  );
  await preparePlayer(rig);
  await _offer(rig);
  return (rig, hold);
}

Future<VerifiedServerDescriptor> _standardServer() => verifiedServer([
  for (final archive in [...StandardJoin.downloads, StandardJoin.gamma])
    archive.package,
]);

/// A local source of the player's that offers every download of the
/// standard join.
Future<void> _offer(JoinRig rig) async {
  final dist = Directory(p.join(rig.root.path, 'source'))..createSync();
  final index = File(p.join(dist.path, 'index.json'))
    ..writeAsStringSync(
      jsonEncode({
        'packages': {
          for (final archive in StandardJoin.downloads)
            archive.id: {
              'versions': {
                archive.version: {
                  ...archive.manifestJson,
                  'url': archive.writeTo(dist).uri.toString(),
                  'zipSHA256': archive.sha256Hex,
                },
              },
            },
        },
      }),
    );
  await rig.repository.savePackageSources([
    PackageSource(
      id: 'join.source',
      name: 'Join Source',
      url: index.uri.toString(),
    ),
  ]);
}

Future<JoinPlan> _plan(JoinRig rig, VerifiedServerDescriptor server) async {
  final plan = const JoinPlanner().planFromCatalog(
    server: server,
    catalog: await rig.repository.loadExactPackageCatalog(rig.install),
    environment: testJoinEnvironment,
    mayContactHost: true,
  );
  expect(plan.isJoinable, isTrue, reason: '${plan.blockers}');
  return plan;
}

/// A whole join as the launcher drives it: a plan from the catalog, the
/// consent, then every content stage through the facade, until it launches
/// or ends.
Future<(JoinSessionState, JoinPlan)> _join(
  JoinRig rig,
  VerifiedServerDescriptor server,
) async {
  final plan = await _plan(rig, server);
  final loopback = InternetAddress.loopbackIPv4;
  final multiplayer = LocalMultiplayerRepository(
    dataRoot: rig.dataRoot,
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
    content: rig.repository,
  );
  addTearDown(multiplayer.dispose);
  var state = consentedJoin(server, plan);
  while (state.stage != JoinStage.launching && !state.isEnded) {
    final event = await multiplayer.answerJoin(
      state,
      install: rig.install,
      onProgress: (progress) => state = JoinSession.reduce(state, progress),
    );
    expect(event, isNotNull, reason: state.stage.name);
    state = JoinSession.reduce(state, event!);
  }
  return (state, plan);
}

JoinPackageDisposition _dispositionOf(JoinPlan plan, String id) =>
    plan.packages.singleWhere((item) => item.package.id == id).disposition;

/// What [profile] pins that is not installed, as its launch preview says.
Future<List<String>> _notInstalledPins(
  JoinRig rig,
  LauncherProfile profile,
) async => [
  for (final issue in (await rig.repository.previewLaunch(
    rig.install,
    profile,
  )).issues)
    if (issue.message.contains('is not installed')) issue.message,
];
