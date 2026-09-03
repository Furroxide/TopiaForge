part of 'topiaforge_cli_test.dart';

/// Moving a project off the retired V5 contract: what the tool carries, and
/// what it refuses to invent.
void _migrationCliTests(_CliTestHarness Function() currentHarness) {
  test('migrate-manifest carries a V5 that declared no gamemodes', () async {
    final projectDir = await _v5Project(currentHarness(), 't.migrate-v5-clean');
    final manifestFile = File(p.join(projectDir, 'topiaforge.mod.json'));

    final result = await currentHarness().runCli([
      'migrate-manifest',
      '--project',
      projectDir,
    ]);

    expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
    final migrated =
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
    expect(migrated['schemaVersion'], ModManifest.currentSchemaVersion);
    expect(migrated, isNot(contains('worldGamemodes')));
    expect(
      ModManifest.fromJson(migrated).validate().where((i) => i.isBlocking),
      isEmpty,
    );
  });

  test('migrate-manifest refuses a V5 that declared gamemodes', () async {
    // The refusal is the feature. A V6 gamemode needs an implementation type, a
    // launch target and world requirements, and a V5 manifest recorded none of
    // them -- so a tool that guessed would write something that validates and
    // then fails at first launch.
    final projectDir = await _v5Project(
      currentHarness(),
      't.migrate-v5-modes',
      worldGamemodes: [
        {'id': 't.migrate-v5-modes.survival', 'name': 'Survival'},
      ],
    );
    final manifestFile = File(p.join(projectDir, 'topiaforge.mod.json'));
    final before = manifestFile.readAsStringSync();

    final refused = await currentHarness().runCli([
      'migrate-manifest',
      '--project',
      projectDir,
    ]);

    expect(refused.exitCode, 1);
    final message = refused.stderr.toString();
    expect(message, contains('t.migrate-v5-modes.survival'));
    expect(message, contains('implementation.type'));
    expect(message, contains('launch target'));
    expect(message, contains('--stub'));
    expect(
      manifestFile.readAsStringSync(),
      before,
      reason: 'a refusal must not leave the manifest half-migrated',
    );

    final stubbed = await currentHarness().runCli([
      'migrate-manifest',
      '--project',
      projectDir,
      '--stub',
    ]);

    // Still non-zero on purpose: the stub is a starting point, not a migration,
    // and a project that cannot be packed by accident is the whole point.
    expect(stubbed.exitCode, 1);
    final written =
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
    expect(written['schemaVersion'], ModManifest.currentSchemaVersion);
    expect(written, isNot(contains('worldGamemodes')));
    expect(written['capabilities'], contains('world-service'));
    final todo = written['x-migration-todo']! as Map<String, Object?>;
    expect((todo['gamemodes']! as List), hasLength(1));

    // The skeleton carries what V5 recorded and omits what it never had, so the
    // ordinary reader rejects it and names the missing field. A stub that
    // validated would be a project that could be packed and published in a
    // state where no gamemode can start.
    expect(
      ModManifest.fromJson(written)
          .validate()
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
      contains(contains('implementation')),
      reason: 'a stubbed manifest must not be publishable',
    );
  });
}

/// Scaffolds a project and rewrites its manifest back to the retired V5 shape.
Future<String> _v5Project(
  _CliTestHarness harness,
  String id, {
  List<Map<String, Object?>> worldGamemodes = const [],
}) async {
  final created = await harness.runCli([
    'new',
    'mod',
    id,
    '--dir',
    harness.temp.path,
  ]);
  expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');

  final projectDir = p.join(harness.temp.path, id);
  final manifestFile = File(p.join(projectDir, 'topiaforge.mod.json'));
  final manifest =
      jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>
        ..remove('contributions')
        ..['schemaVersion'] = 5;
  if (worldGamemodes.isNotEmpty) {
    manifest['worldGamemodes'] = worldGamemodes;
  }
  manifestFile.writeAsStringSync(jsonEncode(manifest));
  return projectDir;
}
