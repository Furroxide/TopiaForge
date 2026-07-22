import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_package_builder.dart';
import 'package:topiaforge/src/release_package_io.dart';
import 'package:topiaforge/src/release_package_models.dart';
import 'package:topiaforge/src/release_package_notices.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('release-sdk-test-');
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  test('compiled CLI prefers the project-local Dart SDK', () async {
    final repo = _writeFixtureRepo(temp);
    final localDart = _writeProjectSdkCommand(repo, 'dart');
    final launcher = _writeLauncherFixture(temp);
    final runner = _RecordingProcessRunner(
      onRun: (call) async {
        if (call.executable == localDart &&
            call.arguments.contains('compile')) {
          _writeCompiledCli(call.arguments);
        }
      },
    );

    await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: p.join(temp.path, 'out'),
      prebuiltLauncher: launcher.path,
      rebuildRuntimePayload: false,
      isAotExecutable: true,
      processRunner: runner,
    ).build();

    final dartCalls = runner.calls
        .where((call) => call.executable == localDart)
        .toList();
    expect(dartCalls, hasLength(2));
    expect(dartCalls.first.arguments, ['pub', 'get', '--enforce-lockfile']);
    expect(dartCalls.last.arguments, containsAllInOrder(['compile', 'exe']));
  });

  test('compiled CLI falls back to Dart on PATH', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = _writeLauncherFixture(temp);
    final runner = _RecordingProcessRunner(
      availableCommands: {'dart'},
      onRun: (call) async {
        if (call.executable == 'dart' && call.arguments.contains('compile')) {
          _writeCompiledCli(call.arguments);
        }
      },
    );

    await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: p.join(temp.path, 'out'),
      prebuiltLauncher: launcher.path,
      rebuildRuntimePayload: false,
      isAotExecutable: true,
      processRunner: runner,
    ).build();

    final dartCalls = runner.calls
        .where((call) => call.executable == 'dart')
        .toList();
    expect(dartCalls, hasLength(2));
    expect(dartCalls.first.arguments, ['pub', 'get', '--enforce-lockfile']);
    expect(dartCalls.last.arguments, containsAllInOrder(['compile', 'exe']));
  });

  test('compiled CLI reports setup guidance when Dart is missing', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = _writeLauncherFixture(temp);

    await expectLater(
      () => ReleasePackageBuilder(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.windows,
        outputRoot: p.join(temp.path, 'out'),
        prebuiltLauncher: launcher.path,
        rebuildRuntimePayload: false,
        isAotExecutable: true,
        processRunner: _RecordingProcessRunner(),
      ).build(),
      _throwsSetupGuidance,
    );
  });

  test('launcher build prefers the project-local Flutter SDK', () async {
    final repo = _writeFixtureRepo(temp);
    final localFlutter = _writeProjectSdkCommand(repo, 'flutter');
    final cli = File(p.join(temp.path, 'topiaforge.exe'))
      ..writeAsStringSync('cli');
    final launcherOutput = File(
      p.join(
        repo.path,
        'apps',
        'topiaforge_launcher_flutter',
        'build',
        'windows',
        'x64',
        'runner',
        'Release',
        'topiaforge_launcher.exe',
      ),
    );
    final runner = _RecordingProcessRunner(
      availableCommands: {'flutter'},
      onRun: (call) async {
        if (call.executable == localFlutter) {
          launcherOutput
            ..parent.createSync(recursive: true)
            ..writeAsStringSync('launcher');
        }
      },
    );

    await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: p.join(temp.path, 'out'),
      prebuiltCli: cli.path,
      rebuildRuntimePayload: false,
      processRunner: runner,
    ).build();

    final flutterCall = runner.calls.singleWhere(
      (call) => call.executable == localFlutter,
    );
    expect(flutterCall.arguments, ['build', 'windows', '--release']);
  });

  test(
    'launcher build reports setup guidance when Flutter is missing',
    () async {
      final repo = _writeFixtureRepo(temp);
      final cli = File(p.join(temp.path, 'topiaforge.exe'))
        ..writeAsStringSync('cli');

      await expectLater(
        () => ReleasePackageBuilder(
          repositoryRoot: repo.path,
          platform: ReleasePackagePlatform.windows,
          outputRoot: p.join(temp.path, 'out'),
          prebuiltCli: cli.path,
          rebuildRuntimePayload: false,
          processRunner: _RecordingProcessRunner(),
        ).build(),
        _throwsSetupGuidance,
      );
    },
  );
}

final _throwsSetupGuidance = throwsA(
  isA<StateError>().having(
    (error) => error.message,
    'message',
    contains('docs/ContributorSetup.md'),
  ),
);

Directory _writeFixtureRepo(Directory temp) {
  final repo = Directory(p.join(temp.path, 'repo'))..createSync();
  File(p.join(repo.path, 'TopiaForge.slnx')).writeAsStringSync('');
  File(p.join(repo.path, 'global.json')).writeAsStringSync(
    jsonEncode({
      'sdk': {
        'version': '10.0.301',
        'rollForward': 'disable',
        'allowPrerelease': false,
      },
    }),
  );
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
  _writeFile(repo, ['docs', 'Guide.md'], 'guide');
  _writeFile(repo, ['bindings', 'binding.txt'], 'binding');
  _writeFile(repo, ['baselines', 'baseline.txt'], 'baseline');
  _writeFile(
    repo,
    ['templates', 'mod', 'minimal', '{{ASSEMBLY_NAME}}.csproj'],
    '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup>
  <ItemGroup><PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" /></ItemGroup>
</Project>''',
  );
  _writeFile(repo, ['dist', 'vpm', 'index.json'], '{}');
  _writeFile(repo, ['dist', 'demo.topiaforgemod'], 'package');
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
  _writeReleaseNoticeFixtures(repo);
  return repo;
}

void _writeReleaseNoticeFixtures(Directory repo) {
  _writeFile(repo, [
    '.fvm',
    'flutter_sdk',
    'bin',
    'cache',
    'dart-sdk',
    'LICENSE',
  ], 'Dart SDK fixture license');
  _writeFile(repo, [
    '.fvm',
    'flutter_sdk',
    'bin',
    'cache',
    'dart-sdk',
    'version',
  ], '3.12.2');

  final packageNames = dartCliLicenseNames
      .where((name) => name.endsWith('-LICENSE.txt'))
      .map((name) => name.substring(0, name.length - '-LICENSE.txt'.length));
  final packages = <Map<String, Object>>[];
  for (final name in packageNames) {
    _writeFile(repo, [
      'apps',
      'topiaforge_cli',
      '.dart_tool',
      'notice-fixtures',
      name,
      'LICENSE',
    ], '$name fixture license');
    _writeFile(repo, [
      'apps',
      'topiaforge_cli',
      '.dart_tool',
      'notice-fixtures',
      name,
      'pubspec.yaml',
    ], 'name: $name\nversion: 1.0.0\n');
    packages.add({
      'name': name,
      'rootUri': 'notice-fixtures/$name/',
      'packageUri': 'lib/',
      'languageVersion': '3.0',
    });
  }
  _writeFile(repo, [
    'apps',
    'topiaforge_cli',
    '.dart_tool',
    'package_config.json',
  ], jsonEncode({'configVersion': 2, 'packages': packages}));
}

Directory _writeLauncherFixture(Directory temp) {
  final launcher = Directory(p.join(temp.path, 'launcher'))
    ..createSync(recursive: true);
  File(
    p.join(launcher.path, 'topiaforge_launcher.exe'),
  ).writeAsStringSync('launcher');
  return launcher;
}

String _writeProjectSdkCommand(Directory repo, String tool) {
  final command = File(
    p.join(
      repo.path,
      '.fvm',
      'flutter_sdk',
      'bin',
      Platform.isWindows ? '$tool.bat' : tool,
    ),
  );
  command
    ..createSync(recursive: true)
    ..writeAsStringSync('fixture');
  return command.path;
}

void _writeCompiledCli(List<String> arguments) {
  final output = _argAfter(arguments, '-o');
  File(output)
    ..parent.createSync(recursive: true)
    ..writeAsStringSync('compiled cli');
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

class _ProcessCall {
  const _ProcessCall(this.executable, this.arguments);

  final String executable;
  final List<String> arguments;
}

class _RecordingProcessRunner extends ReleaseProcessRunner {
  _RecordingProcessRunner({this.availableCommands = const {}, this.onRun});

  final Set<String> availableCommands;
  final Future<void> Function(_ProcessCall call)? onRun;
  final calls = <_ProcessCall>[];

  @override
  Future<bool> commandExists(String executable) async =>
      availableCommands.contains(executable);

  @override
  Future<void> runChecked(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
    Set<String> redactedValueOptions = const {},
  }) async {
    final call = _ProcessCall(executable, List.unmodifiable(arguments));
    calls.add(call);
    await onRun?.call(call);
  }
}
