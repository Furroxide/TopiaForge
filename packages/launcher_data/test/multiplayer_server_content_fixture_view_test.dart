import 'dart:convert';

import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';
import 'multiplayer_network_support.dart';

/// The launcher starts a mod disabled exactly when the game's loader does: a
/// mod that a valid record of `tests/fixtures/multiplayer/server-content`
/// lists in `addedRecords`, found with no manager record, as a game that
/// quit over a join's write, or a deleted manager state, leaves it. The
/// game's loader replays the same cases (`ServerContentFixtureTests`, by
/// `StartsDisabled`), so both stay held to one list.
void main() {
  final fixture = readMultiplayerFixture('server-content/cases.json');
  // The fixtures' records list no version of Arena 1.0.0, so none of them
  // withholds this install; only whether the mod starts off differs.
  final arena = TestArchive('io.example.arena', '1.0.0', name: 'Arena');
  for (final item in (fixture['valid']! as List).cast<Map<String, Object?>>()) {
    final added = (item['addedRecords']! as List).cast<String>();
    final off = added.contains(arena.id);
    test(
      '${item['id']}: with no manager record, Arena starts '
      '${off ? 'off, as the record lists it as added' : 'as any mod does'}',
      () async {
        final rig = await JoinRig.create();
        await rig.installOwn(arena);
        final file = managerStateFile(rig.game);
        final state =
            jsonDecode(file.readAsStringSync()) as Map<String, Object?>;
        (state['mods'] as List).removeWhere(
          (record) => (record as Map)['id'] == arena.id,
        );
        file.writeAsStringSync(jsonEncode(state));
        serverContentFile(
          rig.game,
        ).writeAsBytesSync(utf8.encode(item['bytes']! as String));
        rig.repository = rig.open();
        final snapshot = await rig.repository.loadSnapshot();
        final mod = snapshot.installedMods.singleWhere(
          (candidate) => candidate.id == arena.id,
        );
        expect(mod.enabled, !off);
        final follows = snapshot.profiles.singleWhere(
          (profile) => profile.inheritManagerModState,
        );
        expect(
          await rig.loadsOf(follows),
          off
              ? isNot(contains('io.example.arena@1.0.0'))
              : contains('io.example.arena@1.0.0'),
        );
      },
    );
  }
}
