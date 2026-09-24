import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:topiaforge/src/game_build_bump.dart';
import 'package:test/test.dart';

const _windowsSha =
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const _macSha =
    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const _filesSha =
    'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';
const _exeSha =
    'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd';

void main() {
  late Directory root;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-bump-');
    _write(
      root,
      '.github/robotopia-game-build.json',
      const JsonEncoder.withIndent('  ').convert({
        'buildId': 2409,
        'baseUrl': 'https://builds.example.invalid',
        'manifestUrl': 'https://builds.example.invalid/latest-build.json',
        'sourcePlatform': 'windows',
        'windowsFilesManifest': {
          'path': 'filelist.json',
          'sha256': '0' * 64,
          'fileCount': 409,
          'gameExecutableSha256': '1' * 64,
        },
        'archives': {
          'windows': {'path': 'Robotopia-v02409-Win64.7z', 'sha256': '2' * 64},
          'mac': {'path': 'Robotopia-v02409-Mac.7z', 'sha256': '3' * 64},
        },
      }),
    );
    _write(
      root,
      'release/release-policy.json',
      '{\n  "gameBuild": {\n    "id": 2409\n  }\n}\n',
    );
    _write(
      root,
      'mods/TopiaForge.RobotKit/topiaforge.mod.json',
      '{\n  "supportedGameVersionRange": "0.0.2409"\n}\n',
    );
    _write(
      root,
      'mods/TopiaForge.Zombies/topiaforge.mod.json',
      '{\n  "supportedGameVersionRange": ">=0.0.2409 <0.0.2600"\n}\n',
    );
    _write(
      root,
      'apps/topiaforge_cli/test/release_game_build_policy_test.dart',
      '  const currentBuildId = 2409;\n',
    );
    _write(
      root,
      'tools/release/test-verify-robotopia-install.ps1',
      '        buildId = [Int64]2409 # locked to Robotopia build 2409\n',
    );
    _write(
      root,
      'docs/ReleaseChecklist.md',
      '- [x] Robotopia support is build `2409` (`0.0.2409`).\n',
    );
    _write(
      root,
      'tests/sandbox-workbench-acceptance-v1.json',
      '{\n  "gameBuild": 2409,\n  "scope": "supplementary-offline-contracts"\n}\n',
    );
  });

  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  GameBuildBumpResult bump({
    int to = 2509,
    bool dryRun = false,
    Iterable<String> candidateFiles = const <String>[],
  }) => bumpRobotopiaGameBuild(
    repositoryRoot: root.path,
    toBuildId: to,
    windowsArchiveSha256: _windowsSha,
    macArchiveSha256: _macSha,
    filesManifestSha256: _filesSha,
    filesManifestFileCount: 411,
    gameExecutableSha256: _exeSha,
    dryRun: dryRun,
    candidateFiles: candidateFiles,
  );

  test('rewrites every derivable reference and reports no residue', () {
    final result = bump();

    expect(result.fromBuildId, 2409);
    expect(result.toBuildId, 2509);
    expect(result.isComplete, isTrue, reason: 'residual: ${result.residual}');
    expect(result.totalReplacements, greaterThan(0));

    final pin =
        jsonDecode(_read(root, '.github/robotopia-game-build.json'))
            as Map<String, Object?>;
    expect(pin['buildId'], 2509);
    final archives = pin['archives'] as Map;
    expect((archives['windows'] as Map)['path'], 'Robotopia-v02509-Win64.7z');
    expect((archives['mac'] as Map)['path'], 'Robotopia-v02509-Mac.7z');

    // Hashes are not derivable, so they come from the caller.
    expect((archives['windows'] as Map)['sha256'], _windowsSha);
    expect((archives['mac'] as Map)['sha256'], _macSha);
    final manifest = pin['windowsFilesManifest'] as Map;
    expect(manifest['sha256'], _filesSha);
    expect(manifest['fileCount'], 411);
    expect(manifest['gameExecutableSha256'], _exeSha);

    expect(_read(root, 'release/release-policy.json'), contains('"id": 2509'));
    expect(
      _read(root, 'mods/TopiaForge.RobotKit/topiaforge.mod.json'),
      contains('"0.0.2509"'),
    );
    // A bare integer literal that no quoted pattern would reach.
    expect(
      _read(
        root,
        'apps/topiaforge_cli/test/release_game_build_policy_test.dart',
      ),
      contains('currentBuildId = 2509'),
    );
    // The build-locked install guard, in both its literal and its message.
    final guard = _read(
      root,
      'tools/release/test-verify-robotopia-install.ps1',
    );
    expect(guard, contains('[Int64]2509'));
    expect(guard, contains('build 2509'));
  });

  test('rewrites the supported build that documents state as a fact', () {
    bump();

    expect(
      _read(root, 'docs/ReleaseChecklist.md'),
      contains('build `2509` (`0.0.2509`)'),
    );
    // The Sandbox contract pins the build as a bare JSON integer.
    expect(
      _read(root, 'tests/sandbox-workbench-acceptance-v1.json'),
      contains('"gameBuild": 2509'),
    );
  });

  test('leaves the SDK-only ceiling alone as a judgement call', () {
    bump();
    // The pin moves; the upper bound is deliberately not derived, so the
    // operator is told to review it rather than having it silently guessed.
    expect(
      _read(root, 'mods/TopiaForge.Zombies/topiaforge.mod.json'),
      contains('>=0.0.2509 <0.0.2600'),
    );
  });

  test('a dry run writes nothing', () {
    final before = _read(root, '.github/robotopia-game-build.json');
    final result = bump(dryRun: true);

    expect(result.edits, isNotEmpty);
    expect(_read(root, '.github/robotopia-game-build.json'), before);
    expect(_read(root, 'release/release-policy.json'), contains('"id": 2409'));
  });

  test('reports residue instead of accepting a half-bumped tree', () {
    // A file in the target list that the substitutions cannot reach.
    _write(
      root,
      'release/release-policy.json',
      '{\n  "gameBuild": {\n    "id": 2409\n  },\n  "note": "pinned at 2409 forever"\n}\n',
    );

    final result = bump();

    expect(result.isComplete, isFalse);
    expect(result.residual, contains('release/release-policy.json'));
  });

  test('lists other files that name the old build without failing', () {
    _write(root, 'docs/CreatorScope.md', 'Two subsystems left in 2409.\n');
    _write(root, 'src/Observed.cs', '// On build 2409 the getter throws.\n');
    _write(root, 'docs/internal/Evidence.md', 'Measured on 2409.\n');
    _write(root, 'src/Unrelated.cs', '// Version 124090 is not a build.\n');
    File(p.join(root.path, 'Binary.bin'))
      ..createSync()
      ..writeAsBytesSync([0, ...utf8.encode('2409')]);
    final candidates = [
      'src/Observed.cs',
      'docs/CreatorScope.md',
      'docs/internal/Evidence.md',
      'src/Unrelated.cs',
      'Binary.bin',
      'docs/ReleaseChecklist.md',
      'missing/File.md',
    ];

    final dryRun = bump(dryRun: true, candidateFiles: candidates);
    final result = bump(candidateFiles: candidates);

    const expected = ['docs/CreatorScope.md', 'src/Observed.cs'];
    expect(dryRun.unlistedMentions, expected);
    expect(result.unlistedMentions, expected);
    expect(result.isComplete, isTrue, reason: 'residual: ${result.residual}');
    expect(_read(root, 'src/Observed.cs'), contains('build 2409'));
  });

  test('rejects a malformed hash, a non-positive count, and a no-op bump', () {
    expect(
      () => bumpRobotopiaGameBuild(
        repositoryRoot: root.path,
        toBuildId: 2509,
        windowsArchiveSha256: 'not-a-hash',
        macArchiveSha256: _macSha,
        filesManifestSha256: _filesSha,
        filesManifestFileCount: 411,
        gameExecutableSha256: _exeSha,
      ),
      throwsArgumentError,
    );
    expect(
      () => bumpRobotopiaGameBuild(
        repositoryRoot: root.path,
        toBuildId: 2509,
        windowsArchiveSha256: _windowsSha,
        macArchiveSha256: _macSha,
        filesManifestSha256: _filesSha,
        filesManifestFileCount: 0,
        gameExecutableSha256: _exeSha,
      ),
      throwsArgumentError,
    );
    expect(() => bump(to: 2409), throwsStateError);
  });
}

void _write(Directory root, String relative, String contents) {
  final file = File(p.join(root.path, p.joinAll(relative.split('/'))));
  file.parent.createSync(recursive: true);
  file.writeAsStringSync(contents);
}

String _read(Directory root, String relative) =>
    File(p.join(root.path, p.joinAll(relative.split('/')))).readAsStringSync();
