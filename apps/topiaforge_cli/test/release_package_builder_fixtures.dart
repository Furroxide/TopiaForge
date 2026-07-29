part of 'release_package_builder_test.dart';

Directory _writeFixtureRepo(Directory temp) {
  final repo = Directory(p.join(temp.path, 'repo'))..createSync();
  File(p.join(repo.path, 'TopiaForge.slnx')).writeAsStringSync('');
  File(p.join(repo.path, 'global.json')).writeAsStringSync(
    jsonEncode({
      'sdk': {
        'version': _fixtureDotnetVersion,
        'rollForward': 'disable',
        'allowPrerelease': false,
      },
    }),
  );
  File(p.join(repo.path, '.fvmrc')).writeAsStringSync('{"flutter":"test"}');
  _writeFile(repo, [
    'apps',
    'topiaforge_cli',
    'pubspec.yaml',
  ], 'name: topiaforge\nversion: 0.1.1\n');
  File(p.join(repo.path, 'README.md')).writeAsStringSync('readme');
  File(
    p.join(repo.path, 'THIRD_PARTY_NOTICES.md'),
  ).writeAsStringSync('notices');
  _writeFile(repo, ['tools', 'tool.txt'], 'tool');
  _writeFile(repo, [
    'tools',
    'sidecar',
    'node_modules',
    'dependency.js',
  ], 'generated');
  _writeFile(repo, [
    'tools',
    'sidecar',
    'bin',
    'compiled-host-artifact',
  ], 'generated');
  _writeFile(repo, ['tools', 'sidecar', '.npmrc'], 'token=secret');
  _writeFile(repo, ['docs', 'Guide.md'], 'guide');
  _writeFile(repo, ['docs', 'internal', 'Plan.md'], 'internal');
  _writeFile(repo, ['bindings', 'binding.txt'], 'binding');
  _writeFile(repo, ['baselines', 'baseline.txt'], 'baseline');
  _writeFile(repo, ['templates', 'mod', 'template.txt'], 'template');
  _writeFile(
    repo,
    ['templates', 'mod', 'minimal', '{{ASSEMBLY_NAME}}.csproj'],
    '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup>
  <ItemGroup><PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" /></ItemGroup>
</Project>''',
  );
  _writeFile(repo, ['templates', 'mod', 'bin', 'ignored.txt'], 'ignored');
  _writeFile(repo, [
    'templates',
    'TopiaForge.UnityWorldTemplate',
    'Assets',
    'World.prefab',
  ], 'world source');
  for (final generated in const ['Library', 'Build', 'Logs', 'UserSettings']) {
    _writeFile(repo, [
      'templates',
      'TopiaForge.UnityWorldTemplate',
      generated,
      'generated.txt',
    ], 'generated');
  }
  _writeFile(repo, [
    'templates',
    'TopiaForge.UnityWorldTemplate',
    'TopiaForge.WorldCompanion.Editor.csproj',
  ], 'generated project');
  _writeFile(repo, [
    'templates',
    'TopiaForge.UnityWorldTemplate',
    'TopiaForge.UnityWorldTemplate.sln',
  ], 'generated solution');
  _writeFile(repo, ['dist', 'vpm', 'index.json'], jsonEncode({}));
  _writeFile(repo, ['dist', 'demo.topiaforgemod'], 'package');
  _writeFixtureSdkProjects(repo);
  for (final name in const [
    'TopiaForge.ModPackageValidator.dll',
    'TopiaForge.ModPackageValidator.deps.json',
    'TopiaForge.ModPackageValidator.runtimeconfig.json',
    'TopiaForge.ModManager.Core.dll',
  ]) {
    _writeFile(repo, [
      'src',
      'TopiaForge.ModPackageValidator',
      'bin',
      'Release',
      'net10.0',
      name,
    ], 'validator fixture');
  }
  for (final license in _bepInExLicenseNames) {
    _writeFile(repo, [
      'third_party',
      'BepInEx',
      'LICENSES',
      license,
    ], 'license');
  }
  _writeFile(repo, [
    'src',
    'TopiaForge.GameCompat.Extractor',
    'TopiaForge.GameCompat.Extractor.csproj',
  ], '<Project />');
  _writeFile(repo, [
    '.fvm',
    'flutter_sdk',
    'bin',
    'cache',
    'dart-sdk',
    'LICENSE',
  ], 'Dart SDK license');
  _writeFile(repo, [
    '.fvm',
    'flutter_sdk',
    'bin',
    'cache',
    'dart-sdk',
    'version',
  ], '3.12.2\n');
  final packageEntries = <Map<String, Object>>[];
  for (final package in _dartCliLicensePackageNames) {
    final packageRoot = Directory(
      p.join(repo.path, 'license-packages', package),
    )..createSync(recursive: true);
    _writeFile(repo, [
      'license-packages',
      package,
      'LICENSE',
    ], '$package license');
    _writeFile(repo, [
      'license-packages',
      package,
      'pubspec.yaml',
    ], 'name: $package\nversion: 1.0.0\n');
    packageEntries.add({
      'name': package,
      'rootUri': packageRoot.uri.toString(),
      'packageUri': 'lib/',
      'languageVersion': '3.11',
    });
  }
  _writeFile(repo, [
    'apps',
    'topiaforge_cli',
    '.dart_tool',
    'package_config.json',
  ], jsonEncode({'configVersion': 2, 'packages': packageEntries}));
  for (final runtimeId in const ['osx-x64', 'osx-arm64']) {
    final root = [
      'nuget',
      'microsoft.netcore.app.runtime.$runtimeId',
      '10.0.9',
    ];
    _writeFile(repo, [...root, 'LICENSE.TXT'], '.NET license');
    _writeFile(repo, [...root, 'THIRD-PARTY-NOTICES.TXT'], '.NET notices');
  }
  _writeFile(
    repo,
    [
      'nuget',
      'system.reflection.metadataloadcontext',
      '10.0.9',
      'system.reflection.metadataloadcontext.nuspec',
    ],
    '<package><metadata><license type="expression">MIT</license></metadata></package>',
  );
  _writeFile(repo, [
    'nuget',
    'system.reflection.metadataloadcontext',
    '10.0.9',
    'THIRD-PARTY-NOTICES.TXT',
  ], 'MetadataLoadContext notices');
  return repo;
}

void _writeFixtureSdkProjects(Directory repo) {
  for (final package in topiaForgeSdkPackageIds) {
    final analyzer = topiaForgeAnalyzerPackageIds.contains(package);
    final target = analyzer ? 'netstandard2.0' : 'netstandard2.1';
    _writeFile(
      repo,
      ['src', package, '$package.csproj'],
      '<Project><PropertyGroup><TargetFramework>$target</TargetFramework>'
      '<Version>1.0.0</Version></PropertyGroup></Project>',
    );
    _writeFile(repo, [
      'src',
      package,
      'bin',
      'Release',
      target,
      '$package.dll',
    ], '$package implementation');
    if (!analyzer) {
      _writeFile(repo, [
        'src',
        package,
        'bin',
        'Release',
        target,
        '$package.xml',
      ], '<doc />');
      _writeFile(repo, [
        'src',
        package,
        'obj',
        'Release',
        target,
        'ref',
        '$package.dll',
      ], '$package reference');
    }
  }
}

const _bepInExLicenseNames = [
  'BepInEx-MIT.txt',
  'UnityDoorstop-LGPL-2.1.txt',
  'HarmonyX-MIT.txt',
  'Harmony-MIT.txt',
  'MonoMod-MIT.txt',
  'Mono.Cecil-MIT.txt',
];

final _dartCliLicensePackageNames = dartCliLicenseNames
    .where((name) => name.endsWith('-LICENSE.txt'))
    .map((name) => name.substring(0, name.length - '-LICENSE.txt'.length))
    .toList(growable: false);

void _addDartNotices(Archive archive, {String prefix = ''}) {
  final root = prefix.isEmpty ? '' : '$prefix/';
  for (final name in dartCliLicenseNames) {
    archive.addFile(
      ArchiveFile.string('${root}third_party/dart/LICENSES/$name', 'license'),
    );
  }
}

void _addSdkPayload(Archive archive, {String prefix = ''}) {
  const version = '1.0.0';
  final base = prefix.isEmpty ? '' : '$prefix/';
  final temp = Directory.systemTemp.createTempSync('topiaforge-sdk-fixture-');
  try {
    final references = <String, File>{};
    final documentation = <String, File>{};
    final analyzers = <String, File>{};
    final runtime = <String, File>{};
    for (final package in topiaForgeSdkPackageIds) {
      final dll = File(p.join(temp.path, 'input', '$package.dll'))
        ..createSync(recursive: true)
        ..writeAsStringSync('$package assembly');
      if (topiaForgeAnalyzerPackageIds.contains(package)) {
        analyzers[package] = dll;
        continue;
      }
      references[package] = dll;
      documentation[package] = File(p.join(temp.path, 'input', '$package.xml'))
        ..writeAsStringSync('<doc />');
      if (package == 'TopiaForge.Mods.Testing') runtime[package] = dll;
    }
    final pack = const SdkReferencePackWriter().write(
      destination: Directory(p.join(temp.path, 'sdk', version)),
      sdkVersion: version,
      dotnetSdkVersion: _fixtureDotnetVersion,
      toolVersion: '0.1.1',
      references: references,
      documentation: documentation,
      analyzers: analyzers,
      runtimeAssemblies: runtime,
      runtimeSupportAssemblies: {
        'TopiaForge.Mods.Testing': [
          for (final packageId in topiaForgeTestingRuntimeSupportPackageIds)
            references[packageId]!,
        ],
      },
    );
    for (final file in Directory(
      p.join(temp.path, 'sdk'),
    ).listSync(recursive: true).whereType<File>()) {
      final relative = p.posix.joinAll(
        p.split(p.relative(file.path, from: temp.path)),
      );
      archive.addFile(
        ArchiveFile.bytes('$base$relative', file.readAsBytesSync()),
      );
    }
    archive.addFile(
      ArchiveFile.string(
        '${base}sdk/index.json',
        jsonEncode({
          'schemaVersion': 1,
          'defaultVersion': version,
          'toolVersion': '0.1.1',
          'versions': {
            version: {'manifestSha256': pack.manifestSha256},
          },
        }),
      ),
    );
    archive.addFile(
      ArchiveFile.string(
        '${base}global.json',
        jsonEncode({
          'sdk': {'version': _fixtureDotnetVersion, 'rollForward': 'disable'},
        }),
      ),
    );
    for (final name in const [
      'TopiaForge.ModPackageValidator.dll',
      'TopiaForge.ModPackageValidator.deps.json',
      'TopiaForge.ModPackageValidator.runtimeconfig.json',
      'TopiaForge.ModManager.Core.dll',
    ]) {
      archive.addFile(
        ArchiveFile.string(
          '${base}tools/package-validator/$name',
          'validator fixture',
        ),
      );
    }
    archive.addFile(
      ArchiveFile.string(
        '${base}templates/mod/minimal/Test.csproj',
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>'
            '</PropertyGroup><ItemGroup><PackageReference '
            'Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />'
            '</ItemGroup></Project>',
      ),
    );
  } finally {
    temp.deleteSync(recursive: true);
  }
}

void _writeFile(Directory root, List<String> parts, String content) {
  final file = File(p.joinAll([root.path, ...parts]));
  file.parent.createSync(recursive: true);
  file.writeAsStringSync(content);
}

String _argAfter(List<String> arguments, String option) {
  final index = arguments.indexOf(option);
  if (index < 0 || index + 1 >= arguments.length) {
    throw StateError('Missing $option in ${arguments.join(' ')}');
  }
  return arguments[index + 1];
}

List<int> _markZipEntriesAsUnix(List<int> bytes) {
  final patched = List<int>.of(bytes);
  for (var index = 0; index <= patched.length - 6; index += 1) {
    final isCentralHeader =
        patched[index] == 0x50 &&
        patched[index + 1] == 0x4b &&
        patched[index + 2] == 0x01 &&
        patched[index + 3] == 0x02;
    if (isCentralHeader) {
      patched[index + 5] = 3;
    }
  }
  return patched;
}

ArchiveFile _machOFile(String path) {
  final file = ArchiveFile(path, 8, <int>[0xcf, 0xfa, 0xed, 0xfe, 0, 0, 0, 0]);
  file.mode = 0x1ed;
  return file;
}

class _ProcessCall {
  const _ProcessCall({
    required this.executable,
    required this.arguments,
    required this.workingDirectory,
  });

  final String executable;
  final List<String> arguments;
  final String? workingDirectory;
}

class _RecordingProcessRunner extends ReleaseProcessRunner {
  _RecordingProcessRunner({
    this.availableCommands = const {},
    this.onRun,
    this.onResult,
  });

  final Set<String> availableCommands;
  final Future<void> Function(_ProcessCall call)? onRun;
  final ProcessResult Function(_ProcessCall call)? onResult;
  final calls = <_ProcessCall>[];

  @override
  Future<bool> commandExists(String executable) async =>
      availableCommands.contains(executable);

  @override
  Future<ProcessResult> runResult(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
  }) async {
    final call = _ProcessCall(
      executable: executable,
      arguments: List.unmodifiable(arguments),
      workingDirectory: workingDirectory,
    );
    calls.add(call);
    return onResult?.call(call) ?? ProcessResult(1, 0, '', '');
  }

  @override
  Future<void> runChecked(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
    Set<String> redactedValueOptions = const {},
  }) async {
    final call = _ProcessCall(
      executable: executable,
      arguments: List.unmodifiable(arguments),
      workingDirectory: workingDirectory,
    );
    calls.add(call);
    await onRun?.call(call);
  }
}

Future<DotnetSdkSelection> _resolveFixtureDotnetSdk(
  Directory repositoryRoot,
) async => const DotnetSdkSelection(
  executable: _fixtureDotnetExecutable,
  version: _fixtureDotnetVersion,
  requiredVersion: _fixtureDotnetVersion,
);

const _fixtureDotnetExecutable = '/verified/dotnet';
const _fixtureDotnetVersion = '10.0.301';
