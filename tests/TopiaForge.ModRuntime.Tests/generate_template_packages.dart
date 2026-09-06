import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

// Used by the C# runtime harness: generate real repository projects and preserve
// their source/manifest bytes before compilation. It never rewrites templates.
Future<void> main(List<String> arguments) async {
  if (arguments.length < 2) {
    throw ArgumentError(
      'Expected repository root, output root and optional templates.',
    );
  }
  final repositoryRoot = p.absolute(arguments[0]);
  final output = Directory(p.absolute(arguments[1]));
  if (output.existsSync())
    throw StateError('Generation output already exists.');
  final repository = LocalDeveloperRepository(
    repositoryRoot: repositoryRoot,
    dataRoot: p.join(output.path, 'data'),
  );
  final results = <Map<String, Object?>>[];
  for (final template
      in arguments.skip(2).isEmpty
          ? ['gamemode', 'world']
          : arguments.skip(2)) {
    final workspace = await repository.createModProject(
      parentDirectory: p.join(output.path, 'projects'),
      id: 'tests.generated.$template',
      name: 'Generated $template',
      options: ModScaffoldOptions(
        template: template,
        authorName: 'TopiaForge Integration Tests',
        license: 'MIT',
      ),
    );
    final project = Directory(workspace.projectRoot);
    final manifest = await repository.readModManifest(project.path);
    results.add({
      'template': template,
      'project': project.path,
      'assembly': manifest.entryAssembly,
      'sources': {
        for (final file in project.listSync().whereType<File>().where(
          (file) =>
              file.path.endsWith('.cs') ||
              file.path.endsWith('.csproj') ||
              p.basename(file.path) == 'topiaforge.mod.json',
        ))
          p.basename(file.path): sha256
              .convert(file.readAsBytesSync())
              .toString(),
      },
    });
  }
  File(p.join(output.path, 'generated.json')).writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(results)}\n',
  );
}
