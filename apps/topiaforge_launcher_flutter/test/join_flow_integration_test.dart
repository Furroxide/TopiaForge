import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge_launcher_flutter/src/launcher_bloc.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_event.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_multiplayer_state.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_state.dart';
import 'package:topiaforge_launcher_flutter/src/session_link_inbox.dart';

part 'join_flow_integration_rig.dart';

/// A join through the launcher's own data layer, from a link to the game's
/// start. The service is a fake that resolves the link and hands over a
/// descriptor; everything else is the launcher's: the player's mod source
/// (https, answered from 127.0.0.1) offers the server's mods, which the
/// launcher downloads and installs; it saves the server's profile, writes
/// the game's copy of the preferences, and starts a fake game with launch
/// request V5 as the shared fixtures write it.
void main() {
  test('a link joins through the data layer: the disclosure, consent, mods '
      'from a source on 127.0.0.1, the profile and the preferences, then '
      'request V5', () async {
    final rig = await _Rig.create();
    final archives = [
      _Archive('tf.sandbox', '1.4.0'),
      _Archive('tf.zombies', '2.1.0'),
    ];
    await rig.serve(archives);
    final server = await _descriptor([
      for (final archive in archives) archive.package,
    ]);
    final multiplayer = rig.multiplayer(_Service(server));
    await multiplayer.changePreferences(
      const MultiplayerPreferencesChange(displayName: 'Sam'),
    );
    final inbox = SessionLinkInbox();
    final bloc = LauncherBloc(
      rig.repository,
      multiplayer: multiplayer,
      sessionLinks: inbox.arrivals,
    );
    addTearDown(bloc.close);
    bloc.add(const LauncherStarted());
    await _until(bloc, (state) => state.snapshotLoaded);

    final link = SessionLink.server(server.serverId);
    inbox.deliver(
      SessionLinkArrival(link.canonical, source: SessionLinkSource.forwarded),
    );
    // A link never joins by itself: first the disclosure, then the sheet.
    await _until(bloc, (state) => _flow(state)?.question != null);
    expect(_flow(bloc.state)!.question, JoinQuestion.disclosure);
    bloc.add(
      MultiplayerJoinQuestionAnswered(flow: _flow(bloc.state)!.id, yes: true),
    );
    await _until(
      bloc,
      (state) => _flow(state)?.join?.stage == JoinStage.awaitingConsent,
    );
    expect(
      rig.served.where((path) => path.endsWith('.topiaforgemod')),
      isEmpty,
    );
    expect(rig.game.requests, isEmpty);
    // The game's copy on disk is still the one the launcher's start wrote,
    // from before this join's acknowledgement.
    expect(rig.projectedDisclosure, 0);
    final plan = _flow(bloc.state)!.plan!;
    expect(plan.isJoinable, isTrue, reason: '${plan.blockers}');
    expect(plan.needsDownloads, isTrue);

    bloc.add(MultiplayerJoinConsented(flow: _flow(bloc.state)!.id));
    final launched = await _until(
      bloc,
      (state) =>
          _flow(state)?.launch != null ||
          _flow(state)?.join?.outcome?.failure != null,
      within: const Duration(seconds: 90),
    );
    final flow = _flow(launched)!;
    expect(flow.join!.outcome?.failure?.detail, isNull);
    expect(flow.join!.stage, JoinStage.launching);

    // The mods came from the source on 127.0.0.1, and nowhere else.
    expect(rig.served.where((path) => path.endsWith('.topiaforgemod')), [
      for (final archive in archives) '/${archive.fileName}',
    ]);
    // Besides the player's source, only the launcher's own registry, which
    // every catalog read asks, was contacted; both reached 127.0.0.1.
    expect(rig.hostsAsked, {'mods.example.test', 'docs.topiaforge.dev'});
    for (final archive in archives) {
      expect(
        Directory(
          p.join(
            rig.gameRoot,
            'BepInEx',
            'TopiaForge',
            'packages',
            archive.id,
            archive.version,
          ),
        ).existsSync(),
        isTrue,
        reason: archive.id,
      );
    }
    final profileId = JoinPlanner.derivedProfileId(server.serverId);
    final profiles =
        jsonDecode(
              File(p.join(rig.dataRoot, 'profiles.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    final saved = (profiles['profiles']! as List)
        .cast<Map<String, Object?>>()
        .singleWhere((profile) => profile['id'] == profileId);
    expect(saved['name'], "Server · Sam's Sandbox");

    // The game found its copy of the preferences when it started, written
    // since the acknowledgement: by the round just before the start.
    expect(rig.game.requests, hasLength(1));
    final projected = LaunchTransport.readPreferences(
      rig.game.projections.single!,
    );
    expect(projected.displayName, 'Sam');
    expect(projected.disclosure, greaterThan(0));
    final text = rig.game.requests.single;
    final request = LaunchTransport.readProfileV5(text);
    expect(LaunchTransport.writeProfileV5(request), text);
    final session = request.session;
    final target = session.target! as LaunchListingTarget;
    expect(
      {
        'command': request.command,
        'profileId': request.profileId,
        'role': session.role.name,
        'intent': session.intentId.startsWith('join-'),
        'displayName': session.displayName,
        'developerMode': session.developerMode,
        'privacy': session.transport.privacy.wireName,
        'consented': session.transport.directConsent != null,
        'serviceOrigin': session.serviceOrigin,
        'targetKind': target.kind,
        'source': target.source.name,
        'serverId': target.serverId,
        'descriptorSha256': target.descriptorSha256,
        'packageDigest': target.packageDigest,
        'packages': [for (final package in request.packages) package.id]
          ..sort(),
      },
      {
        'command': 'join-session',
        'profileId': profileId,
        'role': 'join',
        'intent': true,
        'displayName': 'Sam',
        'developerMode': false,
        'privacy': 'relay-only',
        'consented': false,
        'serviceOrigin': 'https://play.example',
        'targetKind': 'listing',
        'source': 'internet',
        'serverId': server.serverId,
        'descriptorSha256': server.descriptorSha256,
        'packageDigest': server.packageDigest,
        'packages': ['tf.sandbox', 'tf.zombies'],
      },
    );
    // Member for member what the shared fixture's Internet listing holds.
    final fixtures =
        jsonDecode(
              File(
                p.join(
                  '..',
                  '..',
                  'tests',
                  'fixtures',
                  'multiplayer',
                  'launch-v5',
                  'cases.json',
                ),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final expected =
        ((fixtures['valid']! as List).cast<Map<String, Object?>>().singleWhere(
              (item) => item['id'] == 'join-internet-listing',
            )['document'])!
            as Map<String, Object?>;
    final document = jsonDecode(text) as Map<String, Object?>;
    Set<String> keys(Object? value) =>
        (value! as Map<String, Object?>).keys.toSet();
    expect(document.keys.toSet(), expected.keys.toSet());
    // A second instance on one machine adds `devInstance`; this is not one.
    expect(
      keys(document['session']),
      keys(expected['session']).difference({'devInstance'}),
    );
    expect(
      keys((document['session']! as Map<String, Object?>)['target']),
      keys((expected['session']! as Map<String, Object?>)['target']),
    );
  }, timeout: const Timeout(Duration(minutes: 3)));
}

JoinFlow? _flow(LauncherState state) => state.multiplayer.join.flow;

Future<LauncherState> _until(
  LauncherBloc bloc,
  bool Function(LauncherState state) test, {
  Duration within = const Duration(seconds: 30),
}) async {
  if (test(bloc.state)) return bloc.state;
  return bloc.stream.firstWhere(test).timeout(within);
}
