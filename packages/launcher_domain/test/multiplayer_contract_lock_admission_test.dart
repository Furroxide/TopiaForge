import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('reports a missing canonical multiplayer contract lock', () {
    const modId = 'example.handcrafted-session';
    final report = const MultiplayerAdmissionPlanner().evaluate(
      server: MultiplayerAdmissionProfile(
        peerId: 'server',
        gameBuild: '0.0.2309',
        topiaForgeProtocolVersion: '1.0.0',
        topiaForgePeerVersionRange: '>=1.0.0 <2.0.0',
        mods: [
          MultiplayerAdmissionMod(
            manifest: ModManifest(
              schemaVersion: ModManifest.currentSchemaVersion,
              id: modId,
              name: 'Handcrafted session',
              version: '1.0.0',
              hashes: {
                'Content/gameplay-rules.json':
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
              },
              multiplayer: ModMultiplayerMetadata.session(
                synchronizedFiles: ['Content/gameplay-rules.json'],
              ),
            ),
          ),
        ],
      ),
      client: MultiplayerAdmissionProfile(
        peerId: 'client',
        gameBuild: '0.0.2309',
        topiaForgeProtocolVersion: '1.0.0',
        topiaForgePeerVersionRange: '>=1.0.0 <2.0.0',
        mods: [],
      ),
    );

    expect(report.isAdmitted, isFalse);
    expect(report.activeSessionMods, isEmpty);
    final mismatch = report.mismatches.single;
    expect(mismatch.code, MultiplayerAdmissionMismatchCode.invalidProfile);
    expect(mismatch.modId, modId);
    expect(
      mismatch.serverValue,
      contains(
        'session synchronized files must include the canonical generated multiplayer contract lock',
      ),
    );
    expect(mismatch.clientValue, isEmpty);
  });
}
