import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('launcher compatibility versions match the bundled runtime', () {
    expect(TopiaForgeRuntimeVersions.loaderVersion, '0.1.0-rc.1');
    expect(TopiaForgeRuntimeVersions.sdkVersion, '0.1.0-rc.1');
    expect(TopiaForgeRuntimeVersions.gameVersion, '0.0.2545');
    final policyFile = [
      File('release/release-policy.json'),
      File('../../release/release-policy.json'),
    ].firstWhere((file) => file.existsSync());
    final policy =
        jsonDecode(policyFile.readAsStringSync()) as Map<String, Object?>;
    final gameBuild = policy['gameBuild'] as Map<String, Object?>;
    expect(
      RobotopiaGameVersion.tryFromBuildId(gameBuild['id']),
      TopiaForgeRuntimeVersions.gameVersion,
    );
  });

  // A join plan predicts the host's admission with the protocol the bundled
  // runtime speaks, so it must be the one the game's contract declares.
  test('the multiplayer protocol is the one the game speaks', () {
    final module = [
      File('src/TopiaForge.Mods.Multiplayer/MultiplayerIdentity.cs'),
      File('../../src/TopiaForge.Mods.Multiplayer/MultiplayerIdentity.cs'),
    ].firstWhere((file) => file.existsSync()).readAsStringSync();
    String constant(String name) => RegExp(
      'public const string $name = "([^"]+)";',
    ).firstMatch(module)!.group(1)!;
    expect(
      TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
      constant('ProtocolVersion'),
    );
    expect(
      TopiaForgeRuntimeVersions.multiplayerPeerVersionRange,
      constant('PeerVersionRange'),
    );
  });
}
