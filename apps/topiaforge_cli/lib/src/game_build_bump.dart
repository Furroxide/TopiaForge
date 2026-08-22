import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

/// One file rewritten by a game-build bump.
class GameBuildBumpEdit {
  const GameBuildBumpEdit(this.path, this.replacements);

  final String path;
  final int replacements;
}

/// The outcome of a bump, including anything the operator must still do by hand.
class GameBuildBumpResult {
  const GameBuildBumpResult({
    required this.fromBuildId,
    required this.toBuildId,
    required this.edits,
    required this.residual,
  });

  final int fromBuildId;
  final int toBuildId;
  final List<GameBuildBumpEdit> edits;

  /// Files that still mention the old build id after the rewrite. A bump that
  /// leaves residue is reported rather than silently accepted, because the
  /// whole point of the command is that no site is missed.
  final List<String> residual;

  bool get isComplete => residual.isEmpty;

  int get totalReplacements =>
      edits.fold(0, (sum, edit) => sum + edit.replacements);
}

/// Rewrites every derivable reference to the pinned Robotopia build.
///
/// Hashes are not derivable from the repository, so the caller supplies them.
/// Everything else — the pin file's build id and zero-padded archive paths, the
/// release policy id, the `0.0.N` ranges in mod manifests, the scaffolder
/// default, runtime version constants, acceptance metadata, and the
/// build-locked test fixtures and guards — is derived from [toBuildId].
///
/// This deliberately does not touch `bindings/` or the compatibility baseline.
/// Those are a reviewed act (`gamecompat verify`, then `gamecompat baseline`),
/// not a find-and-replace.
GameBuildBumpResult bumpRobotopiaGameBuild({
  required String repositoryRoot,
  required int toBuildId,
  required String windowsArchiveSha256,
  required String macArchiveSha256,
  required String filesManifestSha256,
  required int filesManifestFileCount,
  required String gameExecutableSha256,
  bool dryRun = false,
}) {
  if (toBuildId <= 0) {
    throw ArgumentError.value(toBuildId, 'toBuildId', 'must be positive');
  }
  final hashes = <String, String>{
    'windowsArchiveSha256': windowsArchiveSha256,
    'macArchiveSha256': macArchiveSha256,
    'filesManifestSha256': filesManifestSha256,
    'gameExecutableSha256': gameExecutableSha256,
  };
  for (final entry in hashes.entries) {
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(entry.value)) {
      throw ArgumentError.value(
        entry.value,
        entry.key,
        'must be lowercase hex SHA-256',
      );
    }
  }
  if (filesManifestFileCount <= 0) {
    throw ArgumentError.value(
      filesManifestFileCount,
      'filesManifestFileCount',
      'must be positive',
    );
  }

  final pinPath = p.join(
    repositoryRoot,
    '.github',
    'robotopia-game-build.json',
  );
  final pinFile = File(pinPath);
  if (!pinFile.existsSync()) {
    throw StateError('Game build metadata is missing: $pinPath');
  }
  final pin = jsonDecode(pinFile.readAsStringSync()) as Map<String, Object?>;
  final fromBuildId = pin['buildId'];
  if (fromBuildId is! int || fromBuildId <= 0) {
    throw StateError('Game build metadata has no positive buildId.');
  }
  if (fromBuildId == toBuildId) {
    throw StateError('Robotopia build is already pinned to $toBuildId.');
  }

  final from = fromBuildId.toString();
  final to = toBuildId.toString();
  final fromPadded = from.padLeft(5, '0');
  final toPadded = to.padLeft(5, '0');

  // Ordered longest-first so a narrower pattern cannot corrupt a wider one:
  // the archive names and "0.0.N" must be rewritten before any bare literal.
  final substitutions = <String, String>{
    'Robotopia-v$fromPadded-Win64.7z': 'Robotopia-v$toPadded-Win64.7z',
    'Robotopia-v$fromPadded-Mac.7z': 'Robotopia-v$toPadded-Mac.7z',
    '0.0.$from': '0.0.$to',
    'build-$from': 'build-$to',
    'build $from': 'build $to',
    '"id":"$from"': '"id":"$to"',
    "'id': '$from'": "'id': '$to'",
    '"gameBuild": "$from"': '"gameBuild": "$to"',
    '"gameBuildId": $from': '"gameBuildId": $to',
    '"buildId": $from': '"buildId": $to',
    '"id": $from': '"id": $to',
    '== "$from"': '== "$to"',
    '[Int64]$from': '[Int64]$to',
    // Bare integer literals, which no quoted pattern above reaches.
    'currentBuildId = $from': 'currentBuildId = $to',
    '--game-build-id $from': '--game-build-id $to',
  };

  final edits = <GameBuildBumpEdit>[];
  for (final relative in gameBuildBumpTargets) {
    final file = File(p.join(repositoryRoot, relative));
    if (!file.existsSync()) {
      continue;
    }
    final original = file.readAsStringSync();
    var updated = original;
    var count = 0;
    substitutions.forEach((needle, replacement) {
      count += needle.allMatches(updated).length;
      updated = updated.replaceAll(needle, replacement);
    });
    if (updated != original) {
      if (!dryRun) {
        file.writeAsStringSync(updated);
      }
      edits.add(GameBuildBumpEdit(relative, count));
    }
  }

  if (!dryRun) {
    _rewritePinHashes(
      pinFile,
      windowsArchiveSha256: windowsArchiveSha256,
      macArchiveSha256: macArchiveSha256,
      filesManifestSha256: filesManifestSha256,
      filesManifestFileCount: filesManifestFileCount,
      gameExecutableSha256: gameExecutableSha256,
    );
  }

  // Self-check: report anything still naming the old build, so a missed site
  // fails loudly instead of shipping a half-bumped tree. Skipped on a dry run,
  // where nothing was written.
  final residual = <String>[];
  if (!dryRun) {
    final stale = RegExp('(?<![0-9])$from(?![0-9])');
    for (final relative in gameBuildBumpTargets) {
      final file = File(p.join(repositoryRoot, relative));
      if (!file.existsSync()) {
        continue;
      }
      if (stale.hasMatch(file.readAsStringSync())) {
        residual.add(relative);
      }
    }
  }

  return GameBuildBumpResult(
    fromBuildId: fromBuildId,
    toBuildId: toBuildId,
    edits: edits,
    residual: residual,
  );
}

void _rewritePinHashes(
  File pinFile, {
  required String windowsArchiveSha256,
  required String macArchiveSha256,
  required String filesManifestSha256,
  required int filesManifestFileCount,
  required String gameExecutableSha256,
}) {
  final pin = jsonDecode(pinFile.readAsStringSync()) as Map<String, Object?>;
  final archives = (pin['archives'] as Map).cast<String, Object?>();
  (archives['windows'] as Map)['sha256'] = windowsArchiveSha256;
  (archives['mac'] as Map)['sha256'] = macArchiveSha256;
  final manifest = (pin['windowsFilesManifest'] as Map).cast<String, Object?>();
  manifest['sha256'] = filesManifestSha256;
  manifest['fileCount'] = filesManifestFileCount;
  manifest['gameExecutableSha256'] = gameExecutableSha256;
  pinFile.writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(pin)}\n',
  );
}

/// Every repository file that carries a literal Robotopia build id.
///
/// Kept explicit rather than globbed: a bump must be reviewable, and a glob
/// would silently start rewriting unrelated files that happen to contain the
/// number. Anything added later is caught by the residual self-check.
const List<String> gameBuildBumpTargets = <String>[
  '.github/robotopia-game-build.json',
  '.github/workflows/release-package-build.yml',
  'release/release-policy.json',
  'tests/live-game-acceptance.json',
  'tests/TopiaForge.SdkAcceptanceMod/topiaforge.mod.json',
  'tests/fixtures/manifests/v5-valid.json',
  'tests/fixtures/manifests/v5-valid-session.json',
  'tests/fixtures/manifests/v5-valid-extension-payload.json',
  'tests/TopiaForge.ModManager.Tests/FirstPartyManifestTests.cs',
  'tools/release/test-proton.sh',
  'tools/release/test-verify-robotopia-install.ps1',
  'packages/launcher_domain/lib/src/models/runtime_dependency_models.dart',
  'packages/launcher_data/lib/src/local_developer_repository/mod_scaffolding.dart',
  'apps/topiaforge_cli/test/release_game_build_policy_test.dart',
  'apps/topiaforge_cli/test/topiaforge_cli_dev_cases.dart',
  'apps/topiaforge_cli/test/release_package_mod_sdk_helpers.dart',
  'mods/TopiaForge.CreatorContent/CreatorBuiltInCatalog.cs',
  'src/TopiaForge.Mods.Testing/FakeRuntimeInfo.cs',
  'samples/multiplayer/TopiaForge.Multiplayer.CounterSample/topiaforge.mod.json',
  'samples/multiplayer/TopiaForge.Multiplayer.DroneSample/topiaforge.mod.json',
  'mods/TopiaForge.Chronos/topiaforge.mod.json',
  'mods/TopiaForge.CreatorContent/topiaforge.mod.json',
  'mods/TopiaForge.GravityGun/topiaforge.mod.json',
  'mods/TopiaForge.Multiplayer/topiaforge.mod.json',
  'mods/TopiaForge.NoFeedbackUrl/topiaforge.mod.json',
  'mods/TopiaForge.OppositeDay/topiaforge.mod.json',
  'mods/TopiaForge.PerfFixes/topiaforge.mod.json',
  'mods/TopiaForge.Performance/topiaforge.mod.json',
  'mods/TopiaForge.Prompts/topiaforge.mod.json',
  'mods/TopiaForge.RobotKit/topiaforge.mod.json',
  'mods/TopiaForge.Sandbox/topiaforge.mod.json',
  'mods/TopiaForge.UiGallery/topiaforge.mod.json',
  'mods/TopiaForge.Worlds/topiaforge.mod.json',
  'mods/TopiaForge.Zombies/topiaforge.mod.json',
];
