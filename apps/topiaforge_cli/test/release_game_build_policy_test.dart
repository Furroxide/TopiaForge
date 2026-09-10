import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_game_build_policy.dart';
import 'package:test/test.dart';

void main() {
  const currentBuildId = 2409;
  final root = _repositoryRoot();
  final metadata = _json(
    File(p.join(root, '.github', 'robotopia-game-build.json')),
  );
  final baseline = _json(
    File(p.join(root, 'baselines', 'gamecode.surface.baseline.json')),
  );

  test('checked-in game-build release policy is exact', () {
    expect(
      validateRobotopiaGameBuildMetadata(
        metadata: metadata,
        policyBuildId: currentBuildId,
        requireLatestAtRelease: true,
        baseline: baseline,
      ),
      isEmpty,
    );
  });

  for (final mutation
      in <String, void Function(Map<String, Object?>, Map<String, Object?>)>{
        'missing mac archive': (game, _) =>
            (game['archives'] as Map).remove('mac'),
        'extra archive': (game, _) => (game['archives'] as Map)['linux'] = {},
        'wrong archive path': (game, _) =>
            ((game['archives'] as Map)['windows'] as Map)['path'] = 'other.7z',
        'invalid archive hash': (game, _) =>
            ((game['archives'] as Map)['mac'] as Map)['sha256'] = 'abc',
        'missing source platform': (game, _) => game.remove('sourcePlatform'),
        'credentialed base URL': (game, _) =>
            game['baseUrl'] = 'https://token@example.invalid/builds',
        'manifest URL query': (game, _) =>
            game['manifestUrl'] = 'https://example.invalid/latest.json?token=x',
        'baseline mismatch': (_, baseline) =>
            baseline['gameVersion'] = '0.0.2228',
      }.entries) {
    test('rejects ${mutation.key}', () {
      final changedGame = _clone(metadata);
      final changedBaseline = _clone(baseline);
      mutation.value(changedGame, changedBaseline);
      expect(
        validateRobotopiaGameBuildMetadata(
          metadata: changedGame,
          policyBuildId: currentBuildId,
          requireLatestAtRelease: true,
          baseline: changedBaseline,
        ),
        isNotEmpty,
      );
    });
  }

  test('rejects disabling latest-build enforcement', () {
    expect(
      validateRobotopiaGameBuildMetadata(
        metadata: metadata,
        policyBuildId: currentBuildId,
        requireLatestAtRelease: false,
        baseline: baseline,
      ),
      isNotEmpty,
    );
  });

  test('validates a future build without changing validator code', () {
    final changedGame = _clone(metadata);
    final changedBaseline = _clone(baseline);
    changedGame['buildId'] = 2510;
    final archives = changedGame['archives'] as Map;
    (archives['windows'] as Map)['path'] = 'Robotopia-v02510-Win64.7z';
    (archives['mac'] as Map)['path'] = 'Robotopia-v02510-Mac.7z';
    changedBaseline['gameVersionLabel'] = 'build 2510';
    changedBaseline['gameVersion'] = '0.0.2510';

    expect(
      validateRobotopiaGameBuildMetadata(
        metadata: changedGame,
        policyBuildId: 2510,
        requireLatestAtRelease: true,
        baseline: changedBaseline,
      ),
      isEmpty,
    );
  });
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

Map<String, Object?> _clone(Map<String, Object?> value) =>
    (jsonDecode(jsonEncode(value)) as Map).cast<String, Object?>();

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    directory = directory.parent;
  }
  return directory.path;
}
