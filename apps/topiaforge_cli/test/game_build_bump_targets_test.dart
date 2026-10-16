import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:topiaforge/src/game_build_bump.dart';
import 'package:test/test.dart';

/// Guards the checked-in target list itself, so a renamed or emptied target
/// fails here instead of being skipped silently by the next bump.
void main() {
  final root = _repositoryRoot();
  final pin =
      jsonDecode(
            File(
              p.join(root, '.github', 'robotopia-game-build.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  final buildId = '${pin['buildId']}';

  test('every bump target exists and still names the pinned build', () {
    for (final relative in gameBuildBumpTargets) {
      final file = File(p.join(root, p.joinAll(relative.split('/'))));
      expect(file.existsSync(), isTrue, reason: '$relative is missing');
      expect(
        file.readAsStringSync(),
        contains(buildId),
        reason: '$relative no longer names build $buildId',
      );
    }
  });

  test('the target list names each file once', () {
    expect(gameBuildBumpTargets.toSet().length, gameBuildBumpTargets.length);
  });
}

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    directory = directory.parent;
  }
  return directory.path;
}
