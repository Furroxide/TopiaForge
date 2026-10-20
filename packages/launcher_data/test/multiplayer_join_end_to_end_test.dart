import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// A whole join from the player's own sources: the exact catalog, the plan,
/// then every content stage through the facade and the reducer, with no
/// mod matched by hand.
void main() {
  test(
    'joins with exactly the server\'s mods from the player\'s source',
    () async {
      final rig = await JoinRig.create();
      await preparePlayer(rig);
      final dist = Directory(p.join(rig.root.path, 'source'))..createSync();
      final source = File(p.join(dist.path, 'index.json'))
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
          url: source.uri.toString(),
        ),
      ]);
      final server = await verifiedServer([
        for (final archive in [...StandardJoin.downloads, StandardJoin.gamma])
          archive.package,
      ]);
      final catalog = await rig.repository.loadExactPackageCatalog(rig.install);
      final plan = const JoinPlanner().planFromCatalog(
        server: server,
        catalog: catalog,
        environment: testJoinEnvironment,
        mayContactHost: true,
      );
      expect(plan.isJoinable, isTrue, reason: '${plan.blockers}');
      expect(plan.installActions, hasLength(3));
      final before = await rig.playerView();

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
      expect(
        state.stage,
        JoinStage.launching,
        reason: '${state.outcome?.failure?.detail}',
      );

      final snapshot = await rig.repository.loadSnapshot();
      final serverProfile = snapshot.profiles.singleWhere(
        ServerProfiles.isServerProfile,
      );
      expect(await rig.loadsOf(serverProfile), [
        'own.alpha@2.0.0',
        'own.beta@1.5.0',
        'own.gamma@1.0.0',
        'server.delta@1.0.0',
      ]);
      final after = await rig.playerView();
      expect(after.loads, before.loads);
      expect(after.profiles, before.profiles);
      // The receipts say where each archive came from.
      final receipt =
          jsonDecode(
                File(
                  p.join(
                    packageFolder(rig.game, 'server.delta', '1.0.0').path,
                    'topiaforge.install.json',
                  ),
                ).readAsStringSync(),
              )
              as Map;
      expect(receipt['source'], 'registry:join.source');
      expect(receipt['sourceSha256'], StandardJoin.delta.sha256Hex);
      expect(receipt['trust'], 'sha256-verified');
    },
  );
}
