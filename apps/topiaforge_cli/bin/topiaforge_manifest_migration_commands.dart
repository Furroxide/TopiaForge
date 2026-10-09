part of 'topiaforge.dart';

extension _TopiaForgeManifestMigrationCommands on _TopiaForgeCli {
  Future<int> _migrateManifest(List<String> args) async {
    String? projectPath;
    var stub = false;
    void project(String value) {
      if (projectPath != null ||
          value.trim().isEmpty ||
          value.startsWith('--')) {
        throw UsageError(
          'Specify exactly one project path for migrate-manifest.',
        );
      }
      projectPath = value;
    }

    for (var index = 0; index < args.length; index++) {
      final argument = args[index];
      if (argument == '--stub') {
        if (stub) throw UsageError('--stub may be specified only once.');
        stub = true;
      } else if (argument == '--project') {
        if (++index >= args.length) {
          throw UsageError('--project requires a path.');
        }
        project(args[index]);
      } else if (argument.startsWith('--project=')) {
        project(argument.substring('--project='.length));
      } else if (argument.startsWith('-')) {
        throw UsageError('Unknown migrate-manifest option: $argument');
      } else {
        project(argument);
      }
    }
    final file = p.join(
      projectPath ?? Directory.current.path,
      'topiaforge.mod.json',
    );
    const writer = ManifestMigrationWriter();
    final snapshot = await writer.prepare(file);
    final plan = const ManifestMigrationPlanner().plan(
      snapshot.sourceText,
      sourceLabel: snapshot.path,
      mode: stub ? ManifestMigrationMode.stub : ManifestMigrationMode.automatic,
    );
    for (final diagnostic in plan.diagnostics) {
      stderr.writeln('[${diagnostic.code}] $diagnostic');
    }
    switch (plan.disposition) {
      case ManifestMigrationDisposition.unchanged:
        stdout.writeln(
          'topiaforge.mod.json already uses valid schema V6; no files changed.',
        );
        return 0;
      case ManifestMigrationDisposition.refused:
        stderr.writeln(
          'Migration refused; no files changed. Repair the reported fields, '
          'or use --stub to preserve well-formed legacy declarations for author completion.',
        );
        return 1;
      case ManifestMigrationDisposition.invalidStub:
        await writer.commit(snapshot, plan);
        stderr.writeln(
          'Wrote an intentionally invalid V6 stub to ${snapshot.path}. '
          'Complete x-migration-todo before validation or publishing.',
        );
        return 1;
      case ManifestMigrationDisposition.migrated:
        await writer.commit(snapshot, plan);
        stdout.writeln(
          'Migrated topiaforge.mod.json from schema V${plan.sourceVersion} to V6. '
          'Untouched values and property presence are preserved; formatting may change.',
        );
        return 0;
    }
  }
}
