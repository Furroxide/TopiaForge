part of 'release_package_mod_sdk_test.dart';

const _releaseModTemplates = [
  'asset',
  'gamemode',
  'gameplay',
  'minimal',
  'service',
  'ui',
  'world',
];

List<File> _releaseProjectFiles(String projectRoot) => Directory(projectRoot)
    .listSync(recursive: true, followLinks: false)
    .whereType<File>()
    .where((file) => p.extension(file.path) == '.csproj')
    .toList();

File _releaseEntryProject(String projectRoot, List<File> projects) {
  final manifest =
      jsonDecode(
            File(p.join(projectRoot, 'topiaforge.mod.json')).readAsStringSync(),
          )
          as Map<String, Object?>;
  final entryProjectName = p.basenameWithoutExtension(
    manifest['entryAssembly']! as String,
  );
  return projects.singleWhere(
    (file) => p.basenameWithoutExtension(file.path) == entryProjectName,
  );
}

void _expectLocalScaffoldProjectReferences(
  DeveloperWorkspace workspace,
  File project,
  String forbiddenRepositoryRoot,
) {
  final text = project.readAsStringSync();
  final manifest =
      jsonDecode(
            File(
              p.join(workspace.projectRoot, 'topiaforge.mod.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  final apiAssemblies =
      (manifest['apiAssemblies'] as List<Object?>?) ?? const [];
  if (apiAssemblies.isEmpty) {
    expect(text, isNot(contains('<ProjectReference')));
  } else {
    final entryName = p.basenameWithoutExtension(
      manifest['entryAssembly']! as String,
    );
    expect(
      text,
      contains('ProjectReference Include="contracts\\$entryName.Api'),
    );
  }
  expect(text, isNot(contains(forbiddenRepositoryRoot)));
}

void _expectApiAssembliesPacked(
  Archive archive,
  Map<String, Object?> manifest,
) {
  final archivePaths = archive.files.map((file) => file.name).toSet();
  for (final apiAssembly
      in (manifest['apiAssemblies'] as List<Object?>? ?? const [])
          .whereType<String>()) {
    expect(archivePaths, contains(apiAssembly));
  }
}

void _expectReleaseScaffoldLocks(
  String projectRoot, {
  required String sdkVersion,
  required String dotnetVersion,
  required List<String> forbiddenPaths,
}) {
  final global =
      jsonDecode(File(p.join(projectRoot, 'global.json')).readAsStringSync())
          as Map<String, Object?>;
  final sdk = global['sdk'] as Map<String, Object?>;
  expect(sdk['version'], dotnetVersion);
  expect(sdk['rollForward'], 'disable');

  final sdkLock =
      jsonDecode(
            File(
              p.join(projectRoot, 'topiaforge.sdk.lock.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  expect(sdkLock['schemaVersion'], 1);
  expect(sdkLock['sdkVersion'], sdkVersion);
  expect(sdkLock['dotnetSdkVersion'], dotnetVersion);
  expect(sdkLock['manifestSha256'], matches(RegExp(r'^[0-9a-f]{64}$')));

  final projects = Directory(projectRoot)
      .listSync(recursive: true, followLinks: false)
      .whereType<File>()
      .where(
        (file) =>
            p.extension(file.path).toLowerCase() == '.csproj' &&
            !p.split(file.path).contains('obj') &&
            !p.split(file.path).contains('bin'),
      )
      .toList();
  expect(projects, isNotEmpty);
  for (final project in projects) {
    final text = project.readAsStringSync();
    for (final forbidden in forbiddenPaths) {
      expect(text, isNot(contains(forbidden)), reason: project.path);
    }

    for (final match in RegExp(
      r'<ProjectReference\b[^>]*>',
      caseSensitive: false,
    ).allMatches(text)) {
      final include = RegExp(
        r'''\bInclude=["']([^"']+)["']''',
        caseSensitive: false,
      ).firstMatch(match.group(0)!)?.group(1);
      expect(include, isNotNull, reason: project.path);
      final target = p.normalize(
        p.absolute(
          p.join(project.parent.path, include!.replaceAll('\\', p.separator)),
        ),
      );
      expect(
        p.equals(target, projectRoot) || p.isWithin(projectRoot, target),
        isTrue,
        reason: 'ProjectReference escapes scaffold: $target',
      );
      expect(File(target).existsSync(), isTrue, reason: target);
    }

    final packageIds = <String>[];
    for (final match in RegExp(
      r'<PackageReference\b[^>]*>',
      caseSensitive: false,
    ).allMatches(text)) {
      final item = match.group(0)!;
      final include = RegExp(
        r'''\bInclude=["']([^"']+)["']''',
        caseSensitive: false,
      ).firstMatch(item)?.group(1);
      if (include == null || !include.startsWith('TopiaForge.Mods.')) {
        continue;
      }
      final version = RegExp(
        r'''\bVersion=["']([^"']+)["']''',
        caseSensitive: false,
      ).firstMatch(item)?.group(1);
      expect(version, sdkVersion, reason: '$include in ${project.path}');
      packageIds.add(include);
    }
    expect(packageIds, isNotEmpty, reason: project.path);

    final lockFile = File(p.join(project.parent.path, 'packages.lock.json'));
    expect(lockFile.existsSync(), isTrue, reason: project.path);
    final lock =
        jsonDecode(lockFile.readAsStringSync()) as Map<String, Object?>;
    final targets = lock['dependencies'] as Map<String, Object?>;
    for (final packageId in packageIds) {
      Map<Object?, Object?>? entry;
      for (final target in targets.values.whereType<Map>()) {
        final candidate = target[packageId];
        if (candidate is Map) {
          entry = candidate;
          break;
        }
      }
      expect(entry, isNotNull, reason: '$packageId in ${lockFile.path}');
      expect(entry!['resolved'], sdkVersion, reason: packageId);
    }
  }

  final props = File(p.join(projectRoot, 'topiaforge.dev.props'));
  expect(props.existsSync(), isTrue);
  final propsText = props.readAsStringSync();
  for (final forbidden in forbiddenPaths) {
    expect(propsText, isNot(contains(forbidden)));
  }
}

Future<void> _runPackagedCli(
  File cli,
  List<String> arguments, {
  required String workingDirectory,
  required Map<String, String> environment,
}) => _runCheckedProcess(
  cli.path,
  arguments,
  workingDirectory: workingDirectory,
  environment: environment,
);

Future<void> _checkReleaseScaffoldWithPackagedCli(
  File cli,
  String project, {
  required List<String> forbiddenPaths,
  required Map<String, String> environment,
  String? package,
  String? installedPackages,
}) => _runPackagedCli(
  cli,
  [
    'check',
    'scaffold',
    project,
    for (final path in forbiddenPaths) ...['--forbid', path],
    if (package != null) ...['--package', package],
    if (installedPackages != null) ...[
      '--installed-packages',
      installedPackages,
    ],
  ],
  workingDirectory: project,
  environment: environment,
);

Future<void> _expectPackagedCliRejectsNonportableScaffold(
  File cli,
  String project,
  Map<String, String> environment,
) async {
  final result = await Process.run(
    cli.path,
    ['check', 'scaffold', project, '--forbid', project],
    workingDirectory: project,
    environment: environment,
  );
  expect(result.exitCode, isNot(0));
  final diagnostics = result.stderr.toString();
  expect(diagnostics, contains('TFSCF170'));
  expect(
    diagnostics,
    contains('https://docs.topiaforge.dev/diagnostics/TFSCF170'),
  );
}

Future<void> _runCheckedProcess(
  String executable,
  List<String> arguments, {
  required String workingDirectory,
  required Map<String, String> environment,
}) async {
  final result = await Process.run(
    executable,
    arguments,
    workingDirectory: workingDirectory,
    environment: environment,
  );
  expect(
    result.exitCode,
    0,
    reason:
        '$executable ${arguments.join(' ')}\n${result.stdout}\n${result.stderr}',
  );
}

Future<List<String>> _packRuntimeTemplateDependencies(
  File cli,
  Directory repositoryRoot,
  Directory temp,
  Map<String, String> environment,
) async {
  final output = Directory(p.join(temp.path, 'runtime-template-dependencies'))
    ..createSync();
  for (final projectName in const [
    'TopiaForge.Worlds',
    'TopiaForge.RobotKit',
  ]) {
    await _runPackagedCli(
      cli,
      [
        'pack',
        '--project',
        p.join(repositoryRoot.path, 'mods', projectName),
        '--output',
        output.path,
      ],
      workingDirectory: repositoryRoot.path,
      environment: environment,
    );
  }
  final packages =
      output
          .listSync()
          .whereType<File>()
          .where((file) => p.extension(file.path) == '.topiaforgemod')
          .map((file) => file.path)
          .toList()
        ..sort();
  expect(packages, hasLength(2));
  return packages;
}

Directory _createSdkSmokeGame(Directory temp) {
  final root = Directory(p.join(temp.path, 'fake-game'))..createSync();
  File(p.join(root.path, 'Robotopia.exe')).writeAsStringSync('game');
  final managed = Directory(p.join(root.path, 'Robotopia_Data', 'Managed'))
    ..createSync(recursive: true);
  File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('unity');
  File(
    p.join(root.path, 'installed-build.json'),
  ).writeAsStringSync(jsonEncode({'id': '2227'}));
  return root;
}

Future<void> _expectPackagedCliRejectsBadPe(
  File cli,
  Directory temp,
  Map<String, String> environment,
) async {
  final package = File(p.join(temp.path, 'bad-pe.topiaforgemod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 4,
          'name': 'test.packaged.bad-pe',
          'displayName': 'Packaged Bad PE',
          'version': '1.0.0',
          'author': {'name': 'Release Smoke'},
          'entryAssembly': 'Bad.dll',
          'entryType': 'Test.BadMod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('Bad.dll', 'not a managed PE'));
  package.writeAsBytesSync(ZipEncoder().encode(archive));

  final rejected = await Process.run(
    cli.path,
    ['check', 'package', package.path],
    workingDirectory: cli.parent.path,
    environment: environment,
  );
  expect(rejected.exitCode, isNot(0));
  expect(rejected.stderr.toString(), contains('TFPKG160'));
  expect(rejected.stderr.toString(), contains('not a valid PE image'));
}
