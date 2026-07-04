import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('robotopia-cli-test-');
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  // Keep the VCC-style registry and data files inside the temp dir so tests never touch the real APPDATA state.
  Future<ProcessResult> runCli(List<String> args) {
    return Process.run(
      Platform.resolvedExecutable,
      ['run', 'robotopia', ...args],
      workingDirectory: Directory.current.path,
      environment: {
        ...Platform.environment,
        'ROBOTOPIA_DATA_ROOT': p.join(temp.path, 'data'),
      },
    );
  }

  test('prints help for the public robotopia executable', () async {
    final result = await runCli(['help']);

    expect(result.exitCode, 0);
    expect(result.stdout.toString(), contains('robotopia new mod'));
    expect(result.stdout.toString(), contains('robotopia restore'));
    expect(result.stdout.toString(), contains('robotopia updates index'));
    expect(result.stdout.toString(), contains('robotopia mod set'));
    expect(result.stdout.toString(), contains('robotopia ugc setup'));
    expect(result.stdout.toString(), contains('robotopia ugc dev'));
    expect(result.stdout.toString(), contains('Getting started:'));
    expect(result.stdout.toString(), contains('Build & run:'));
    expect(result.stdout.toString(), contains('Project & manifest:'));
  });

  test('unknown command exits 1 with a short pointer, not the full help', () async {
    final result = await runCli(['definitely-not-a-command']);

    expect(result.exitCode, 1);
    final errText = result.stderr.toString();
    expect(errText, contains('Unknown command: definitely-not-a-command'));
    expect(errText, contains('robotopia help'));
    final combined = '${result.stdout}$errText';
    expect(combined, isNot(contains('robotopia ugc watch')));
  });

  test('unknown command suggests a near-miss command', () async {
    final result = await runCli(['isntall']);

    expect(result.exitCode, 1);
    expect(result.stderr.toString(), contains('Did you mean: robotopia install?'));
  });

  test('check without a subcommand prints usage and exits 1', () async {
    final result = await runCli(['check']);

    expect(result.exitCode, 1);
    expect(
      result.stderr.toString(),
      contains('Usage: robotopia check project|package'),
    );
  });

  test('check package on a nonexistent path fails with guidance', () async {
    final result = await runCli([
      'check',
      'package',
      p.join(temp.path, 'does-not-exist'),
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString().trim(), isNotEmpty);
  });

  test('scaffolds a gamemode mod with flag overrides that passes check package', () async {
    final created = await runCli([
      'new',
      'mod',
      't.demo',
      '--template',
      'gamemode',
      '--name',
      'Demo Mode',
      '--dir',
      temp.path,
      '--tag',
      'alpha',
      '--tag',
      'beta',
      '--permission',
      'hud',
      '--dependency',
      'robotopia.chronos@>=0.1.0',
      '--author',
      'Charl',
      '--license',
      'MIT',
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');

    final projectDir = p.join(temp.path, 't.demo');
    final manifest =
        jsonDecode(
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest[r'$schema'], contains('robotopia.mod.schema.json'));
    expect(manifest['displayName'], 'Demo Mode');
    expect(manifest['tags'], ['alpha', 'beta']);
    expect((manifest['author'] as Map)['name'], 'Charl');
    expect(
      (manifest['vpmDependencies'] as Map).keys,
      containsAll(['robotopia.worlds', 'robotopia.robotkit', 'robotopia.chronos']),
    );
    expect(manifest['worldGamemodes'], isNotEmpty);

    final checked = await runCli(['check', 'package', projectDir]);
    expect(checked.exitCode, 0, reason: '${checked.stdout}\n${checked.stderr}');
  });

  test('mod set and mod add edit the manifest with validation', () async {
    final created = await runCli([
      'new',
      'mod',
      't.editable',
      '--dir',
      temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(temp.path, 't.editable');

    final setResult = await runCli([
      'mod',
      'set',
      'version',
      '0.2.0',
      '--project',
      projectDir,
    ]);
    expect(setResult.exitCode, 0, reason: '${setResult.stdout}\n${setResult.stderr}');

    final addResult = await runCli([
      'mod',
      'add',
      'permission',
      'time',
      '--project',
      projectDir,
    ]);
    expect(addResult.exitCode, 0, reason: '${addResult.stdout}\n${addResult.stderr}');

    final manifest =
        jsonDecode(
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest['version'], '0.2.0');
    expect(manifest['permissions'], contains('time'));

    // An invalid edit is refused instead of written.
    final badResult = await runCli([
      'mod',
      'set',
      'version',
      'not-a-version',
      '--project',
      projectDir,
    ]);
    expect(badResult.exitCode, 1);
    final unchanged =
        jsonDecode(
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(unchanged['version'], '0.2.0');
  });

  test('ugc setup --no-deploy persists live-sync settings into the project', () async {
    final created = await runCli([
      'new',
      'mod',
      't.synced',
      '--dir',
      temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(temp.path, 't.synced');
    final watchDir = p.join(temp.path, 'watch');

    final setupResult = await runCli([
      'ugc',
      'setup',
      '--watch',
      watchDir,
      '--auto-connect',
      '--scene',
      'main',
      '--no-deploy',
      '--project',
      projectDir,
    ]);
    expect(
      setupResult.exitCode,
      0,
      reason: '${setupResult.stdout}\n${setupResult.stderr}',
    );

    final project =
        jsonDecode(
              File(
                p.join(projectDir, 'robotopia.project.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final liveSync =
        ((project['unityCompanion'] as Map)['liveSync'] as Map)
            .cast<String, Object?>();
    expect(liveSync['watchFolder'], p.normalize(p.absolute(watchDir)));
    expect(liveSync['sceneId'], 'main');
    expect(liveSync['autoConnectOnStart'], isTrue);
    expect(Directory(watchDir).existsSync(), isTrue);
  });

  test('ugc dev --dry-run resolves the plan without launching anything', () async {
    // A bare Unity-shaped project is enough for resolution.
    final unityProject = Directory(p.join(temp.path, 'World'))
      ..createSync(recursive: true);
    Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
    Directory(p.join(unityProject.path, 'Assets')).createSync();

    final result = await runCli([
      'ugc',
      'dev',
      '--project',
      unityProject.path,
      '--watch',
      p.join(temp.path, 'watch'),
      '--dry-run',
    ]);
    expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
    expect(result.stdout.toString(), contains('[dry-run]'));
    expect(result.stdout.toString(), contains('Watch folder'));
    // Dry run leaves the project untouched.
    expect(
      File(
        p.join(
          unityProject.path,
          'ProjectSettings',
          'RobotopiaUgcCompanion.json',
        ),
      ).existsSync(),
      isFalse,
    );
  });

  test('prints help for the update index command', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      'run',
      'robotopia',
      'updates',
      'index',
      '--help',
    ], workingDirectory: Directory.current.path);

    expect(result.exitCode, 0);
    expect(
      result.stdout.toString(),
      contains('Usage: robotopia updates index'),
    );
  });

  test('lists built-in templates', () async {
    final result = await runCli(['list', 'templates']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    for (final template in [
      'minimal',
      'gameplay',
      'gamemode',
      'service',
      'ui',
      'asset',
      'world',
    ]) {
      expect(output, contains('--template $template'));
    }
    expect(output, contains('unity-world'));
  });

  test('help covers the world authoring commands', () async {
    final result = await runCli(['help']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    expect(output, contains('robotopia world link'));
    expect(output, contains('robotopia world build'));
    expect(output, contains('robotopia world play'));
  });

  test('scaffolds a world mod that passes check package', () async {
    final created = await runCli([
      'new',
      'mod',
      't.island',
      '--template',
      'world',
      '--name',
      'Sky Island',
      '--dir',
      temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');

    final projectDir = p.join(temp.path, 't.island');
    final manifest =
        jsonDecode(
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (manifest['vpmDependencies'] as Map).keys,
      containsAll(['robotopia.assets', 'robotopia.worlds']),
    );
    expect(manifest['permissions'], contains('asset-bundles'));
    expect(manifest['permissions'], contains('world-service'));

    // The scaffolded mod code registers the bundle world with the derived bundle name.
    final modSource = File(
      p.join(projectDir, 'TIslandMod.cs'),
    ).readAsStringSync();
    expect(modSource, contains('RegisterWorldFromBundle'));
    expect(modSource, contains('AssetBundles/t-island.bundle'));

    final checked = await runCli(['check', 'package', projectDir]);
    expect(checked.exitCode, 0, reason: '${checked.stdout}\n${checked.stderr}');
  });

  test('world link pairs a Unity project with a mod', () async {
    final created = await runCli([
      'new',
      'mod',
      't.paired',
      '--template',
      'world',
      '--dir',
      temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final modDir = p.join(temp.path, 't.paired');

    final unityProject = Directory(p.join(temp.path, 'PairedWorld'))
      ..createSync(recursive: true);
    Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
    Directory(p.join(unityProject.path, 'Assets')).createSync();

    final linked = await runCli([
      'world',
      'link',
      '--project',
      unityProject.path,
      '--mod',
      modDir,
    ]);
    expect(linked.exitCode, 0, reason: '${linked.stdout}\n${linked.stderr}');

    final config =
        jsonDecode(
              File(
                p.join(unityProject.path, 'robotopia.world.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(config['worldId'], 't.paired');
    expect(config['bundleName'], 't-paired');
    expect(config['worldPrefab'], 'Assets/World/World.prefab');

    // Dry run resolves the pairing without launching Unity.
    final dryRun = await runCli([
      'world',
      'build',
      '--project',
      unityProject.path,
      '--dry-run',
    ]);
    final dryRunOutput = '${dryRun.stdout}\n${dryRun.stderr}';
    expect(dryRunOutput, contains('Paired mod'));
    expect(dryRunOutput, contains('t-paired'));
  });

  test('help covers the UI bundle build command', () async {
    final result = await runCli(['help']);

    expect(result.exitCode, 0);
    expect(
      result.stdout.toString(),
      contains('robotopia unity build-ui-bundle'),
    );
  });

  test('unity build-ui-bundle --dry-run resolves the plan without launching Unity', () async {
    final result = await runCli(['unity', 'build-ui-bundle', '--dry-run']);

    // Editor availability is machine-dependent; assert only the printed structure.
    final output = '${result.stdout}\n${result.stderr}';
    expect(output, contains('Unity project:'));
    expect(
      output,
      contains(p.join('tools', 'unity-ui-bundle')),
      reason: output,
    );
    expect(output, contains('Build editor:'));
    expect(output, contains('ui-bundle-build.log'));
  });

  test('unity build-ui-bundle rejects a nonexistent --unity editor', () async {
    final result = await runCli([
      'unity',
      'build-ui-bundle',
      '--unity',
      p.join(temp.path, 'no-such', 'Unity.exe'),
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString(), contains('Unity editor not found'));
  });

  test('unity build-ui-bundle gates an ineligible --unity editor version', () async {
    // Hub-layout folder named after a too-new editor stream.
    final editorDir = Directory(
      p.join(temp.path, 'Hub', 'Editor', '6000.5.1f1', 'Editor'),
    )..createSync(recursive: true);
    final fakeEditor = File(p.join(editorDir.path, 'Unity.exe'))
      ..writeAsStringSync('not a real editor');

    final result = await runCli([
      'unity',
      'build-ui-bundle',
      '--unity',
      fakeEditor.path,
    ]);

    expect(result.exitCode, 1);
    final errText = result.stderr.toString();
    expect(errText, contains('6000.5.1f1'));
    expect(errText, contains('6000.0.x'));
  });

  test('world build without a pairing points at world link', () async {
    final unityProject = Directory(p.join(temp.path, 'Unpaired'))
      ..createSync(recursive: true);
    Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
    Directory(p.join(unityProject.path, 'Assets')).createSync();

    final result = await runCli([
      'world',
      'build',
      '--project',
      unityProject.path,
    ]);
    expect(result.exitCode, 1);
    expect('${result.stdout}\n${result.stderr}', contains('world link'));
  });
}
