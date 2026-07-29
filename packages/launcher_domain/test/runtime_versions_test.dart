import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('launcher compatibility versions match the bundled runtime', () {
    expect(TopiaForgeRuntimeVersions.loaderVersion, '1.0.0-rc.1');
    expect(TopiaForgeRuntimeVersions.sdkVersion, '1.0.0-rc.1');
    expect(TopiaForgeRuntimeVersions.gameVersion, '0.0.2309');
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
}
