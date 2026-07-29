import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_package_io.dart';
import 'package:topiaforge/src/release_loader_payload.dart';
import 'package:topiaforge/src/release_package_models.dart';
import 'package:topiaforge/src/release_package_notices.dart';
import 'package:topiaforge/src/release_package_payload.dart';
import 'package:topiaforge/src/release_sdk_payload.dart';

part 'release_package_mod_sdk_helpers.dart';
part 'release_package_runtime_loader_cases.dart';

void main() {
  final repositoryRoot = Directory(
    p.normalize(p.join(Directory.current.path, '..', '..')),
  ).absolute;
  late Directory temp;
  late DotnetSdkSelection dotnet;

  setUpAll(() async {
    dotnet = await resolveRepositoryDotnetSdk(repositoryRoot);
    final restore = await Process.run(dotnet.executable, [
      'restore',
      p.join(repositoryRoot.path, 'TopiaForge.slnx'),
      '--nologo',
    ], workingDirectory: repositoryRoot.path);
    expect(restore.exitCode, 0, reason: '${restore.stdout}\n${restore.stderr}');
    final projects = Directory(p.join(repositoryRoot.path, 'src'))
        .listSync()
        .whereType<Directory>()
        .where((directory) {
          final name = p.basename(directory.path);
          return topiaForgeSdkPackageIds.contains(name);
        });
    for (final directory in projects) {
      final name = p.basename(directory.path);
      final project = p.join(directory.path, '$name.csproj');
      if (!File(project).existsSync()) continue;
      final build = await Process.run(dotnet.executable, [
        'build',
        project,
        '-c',
        'Release',
        '--no-restore',
        '--nologo',
      ], workingDirectory: repositoryRoot.path);
      expect(
        build.exitCode,
        0,
        reason: '$name\n${build.stdout}\n${build.stderr}',
      );
    }
    final validator = await Process.run(dotnet.executable, [
      'build',
      p.join(
        repositoryRoot.path,
        'src',
        'TopiaForge.ModPackageValidator',
        'TopiaForge.ModPackageValidator.csproj',
      ),
      '-c',
      'Release',
      '--nologo',
    ], workingDirectory: repositoryRoot.path);
    expect(
      validator.exitCode,
      0,
      reason: '${validator.stdout}\n${validator.stderr}',
    );
  });

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-relocatable-sdk-');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  _registerRuntimeLoaderPayloadTests(
    repositoryRoot: repositoryRoot,
    temp: () => temp,
  );

  test('release SDK payload is a canonical reference-only NuGet feed', () {
    final payload = Directory(p.join(temp.path, 'sdk-payload'))
      ..createSync(recursive: true);
    final pack = ReleaseSdkPayloadWriter(
      repositoryRoot: repositoryRoot.path,
      configuration: 'Release',
    ).write(payload.path);
    const ReleaseFileOps().copyDirectory(
      Directory(p.join(repositoryRoot.path, 'templates')),
      Directory(p.join(payload.path, 'templates')),
    );
    const ReleaseSdkPayloadValidator().validate(payload.path);
    expect(
      pack.packages.map((package) => package.id).toSet(),
      topiaForgeSdkPackageIds,
    );
    expect(
      pack.packages.map((package) => package.id),
      isNot(contains('TopiaForge.Mods.UnityUi')),
      reason: 'the Unity renderer is loader-owned, not a mod SDK contract',
    );
    expect(
      File(
        p.join(
          repositoryRoot.path,
          'src',
          'TopiaForge.Mods.UnityUi',
          'TopiaForge.Mods.UnityUi.csproj',
        ),
      ).readAsStringSync(),
      contains('<IsPackable>false</IsPackable>'),
    );
    for (final package in pack.packages.where(
      (package) => package.kind == 'contract',
    )) {
      final archive = ZipDecoder().decodeBytes(
        File(p.join(pack.root.path, package.path)).readAsBytesSync(),
        verify: true,
      );
      expect(
        archive.files.map((file) => file.name),
        isNot(contains(startsWith('lib/'))),
        reason: package.id,
      );
    }
    final interop = pack.packages.singleWhere(
      (package) => package.id == 'TopiaForge.Mods.Interop.Unity',
    );
    final archive = ZipDecoder().decodeBytes(
      File(p.join(pack.root.path, interop.path)).readAsBytesSync(),
    );
    final props = archive.files.singleWhere(
      (file) =>
          file.name == 'buildTransitive/TopiaForge.Mods.Interop.Unity.props',
    );
    final propsText = utf8.decode(props.content as List<int>);
    expect(propsText, contains('TopiaForgeSafeProject'));
    expect(propsText, contains('TF1101'));
  });

  test(
    'packaged CLI owns the relocated seven-template lifecycle',
    () async {
      final payload = Directory(p.join(temp.path, 'extracted-release'))
        ..createSync(recursive: true);
      final sdkPack = ReleaseSdkPayloadWriter(
        repositoryRoot: repositoryRoot.path,
        configuration: 'Release',
      ).write(payload.path);
      const ReleaseFileOps().copyDirectory(
        Directory(p.join(repositoryRoot.path, 'templates')),
        Directory(p.join(payload.path, 'templates')),
      );
      const ReleaseFileOps().copyDirectory(
        Directory(p.join(repositoryRoot.path, 'tools')),
        Directory(p.join(payload.path, 'tools')),
      );
      final cli = File(p.join(payload.path, 'topiaforge'));
      final compiled = await Process.run(
        Platform.resolvedExecutable,
        ['compile', 'exe', 'bin/topiaforge.dart', '-o', cli.path],
        workingDirectory: p.join(repositoryRoot.path, 'apps', 'topiaforge_cli'),
      );
      expect(
        compiled.exitCode,
        0,
        reason: '${compiled.stdout}\n${compiled.stderr}',
      );

      final dataRoot = Directory(p.join(temp.path, 'persistent-user-data'))
        ..createSync();
      final fakeGame = _createSdkSmokeGame(temp);
      final environment = {
        ...Platform.environment,
        'TOPIAFORGE_DATA_ROOT': dataRoot.path,
        'ROBOTOPIA_GAME_DIR': fakeGame.path,
        'NUGET_PACKAGES': p.join(temp.path, 'nuget-packages'),
        'DOTNET_CLI_HOME': p.join(temp.path, 'dotnet-home'),
      };
      final runtimePackages = await _packRuntimeTemplateDependencies(
        cli,
        repositoryRoot,
        temp,
        environment,
      );
      final staged = Directory(p.join(temp.path, 'outside-scaffolds'))
        ..createSync();
      const templates = _releaseModTemplates;
      final projectPaths = <String>[];
      for (final template in templates) {
        final parent = Directory(p.join(staged.path, template))..createSync();
        final id = 'test.packaged.$template';
        await _runPackagedCli(
          cli,
          [
            'new',
            'mod',
            id,
            '--template',
            template,
            '--dir',
            parent.path,
            '--author',
            'Release Smoke',
            '--license',
            'MIT',
          ],
          workingDirectory: payload.path,
          environment: environment,
        );
        final project = p.join(parent.path, id);
        await _runPackagedCli(
          cli,
          ['restore', '--project', project],
          workingDirectory: payload.path,
          environment: environment,
        );
        _expectReleaseScaffoldLocks(
          project,
          sdkVersion: sdkPack.version,
          dotnetVersion: dotnet.requiredVersion,
          forbiddenPaths: [payload.path, repositoryRoot.path],
        );
        projectPaths.add(project);
      }

      final relocatedRoot = Directory(p.join(temp.path, 'relocated-projects'))
        ..createSync();
      final relocated = <String>[];
      for (final project in projectPaths) {
        final target = p.join(relocatedRoot.path, p.basename(project));
        Directory(project).renameSync(target);
        relocated.add(target);
      }
      final relocatedCliDirectory = Directory(
        p.join(temp.path, 'relocated-cli'),
      )..createSync();
      final relocatedCli = cli.copySync(
        p.join(relocatedCliDirectory.path, p.basename(cli.path)),
      );
      if (!Platform.isWindows) {
        await Process.run('chmod', ['+x', relocatedCli.path]);
      }
      File(
        p.join(payload.path, 'global.json'),
      ).copySync(p.join(relocatedCliDirectory.path, 'global.json'));
      const ReleaseFileOps().copyDirectory(
        Directory(p.join(payload.path, 'tools', 'package-validator')),
        Directory(
          p.join(relocatedCliDirectory.path, 'tools', 'package-validator'),
        ),
      );
      payload.deleteSync(recursive: true);
      await _expectPackagedCliRejectsNonportableScaffold(
        relocatedCli,
        relocated.first,
        environment,
      );

      final packagesToInstall = <String>[];
      for (final project in relocated) {
        await _checkReleaseScaffoldWithPackagedCli(
          relocatedCli,
          project,
          forbiddenPaths: [payload.path, repositoryRoot.path],
          environment: environment,
        );
        final projectFiles = _releaseProjectFiles(project);
        final entryProject = _releaseEntryProject(project, projectFiles);
        await _runCheckedProcess(
          dotnet.executable,
          [
            'build',
            entryProject.path,
            '-c',
            'Release',
            '--no-restore',
            '--nologo',
          ],
          workingDirectory: project,
          environment: environment,
        );
        final testProjects = projectFiles
            .where((file) => p.basename(file.path).contains('.Tests.'))
            .toList();
        expect(testProjects, isNotEmpty, reason: p.basename(project));
        for (final testProject in testProjects) {
          await _runCheckedProcess(
            dotnet.executable,
            [
              'test',
              testProject.path,
              '-c',
              'Release',
              '--no-restore',
              '--nologo',
            ],
            workingDirectory: project,
            environment: environment,
          );
        }

        final output = Directory(p.join(project, 'package-output'))
          ..createSync();
        await _runPackagedCli(
          relocatedCli,
          ['pack', '--project', project, '--output', output.path],
          workingDirectory: project,
          environment: environment,
        );
        final package = output.listSync().whereType<File>().singleWhere(
          (file) => p.extension(file.path) == '.topiaforgemod',
        );
        packagesToInstall.add(package.path);
        await _runPackagedCli(
          relocatedCli,
          ['check', 'package', package.path],
          workingDirectory: project,
          environment: environment,
        );
      }
      expect(packagesToInstall, hasLength(templates.length));
      for (final package in [...runtimePackages, ...packagesToInstall]) {
        await _runPackagedCli(
          relocatedCli,
          ['install', package],
          workingDirectory: relocatedRoot.path,
          environment: environment,
        );
      }

      final installedPackages = p.join(
        fakeGame.path,
        'BepInEx',
        'TopiaForge',
        'packages',
      );
      for (var index = 0; index < relocated.length; index++) {
        await _checkReleaseScaffoldWithPackagedCli(
          relocatedCli,
          relocated[index],
          forbiddenPaths: [payload.path, repositoryRoot.path],
          package: packagesToInstall[index],
          installedPackages: installedPackages,
          environment: environment,
        );
      }

      await _expectPackagedCliRejectsBadPe(relocatedCli, temp, environment);

      for (final project in relocated) {
        final projectFiles = Directory(project)
            .listSync(recursive: true, followLinks: false)
            .whereType<File>()
            .where((file) => p.extension(file.path) == '.csproj');
        for (final projectFile in projectFiles) {
          await _runCheckedProcess(
            dotnet.executable,
            [
              'build',
              projectFile.path,
              '-c',
              'Release',
              '--no-restore',
              '--nologo',
            ],
            workingDirectory: project,
            environment: environment,
          );
        }
      }
    },
    timeout: const Timeout(Duration(minutes: 5)),
  );

  test(
    'all release templates build and pack after payload removal',
    () async {
      final payload = Directory(p.join(temp.path, 'release-payload'))
        ..createSync(recursive: true);
      final releasePack = ReleaseSdkPayloadWriter(
        repositoryRoot: repositoryRoot.path,
        configuration: 'Release',
      ).write(payload.path);
      expect(
        releasePack.packages.map((package) => package.id).toSet(),
        topiaForgeSdkPackageIds,
      );
      expect(
        releasePack.packages
            .where(
              (package) =>
                  package.kind == 'contract' &&
                  package.id != 'TopiaForge.Mods.Interop.Unity',
            )
            .every((package) => !package.hasRuntimeAssembly),
        isTrue,
      );
      const ReleaseFileOps().copyDirectory(
        Directory(p.join(repositoryRoot.path, 'templates')),
        Directory(p.join(payload.path, 'templates')),
      );
      Directory(p.join(payload.path, 'tools')).createSync();
      Directory(p.join(payload.path, 'dist')).createSync();

      final projects = Directory(p.join(temp.path, 'projects'))..createSync();
      final repository = LocalDeveloperRepository(
        dataRoot: p.join(temp.path, 'persistent-user-data'),
        repositoryRoot: payload.path,
      );
      final workspaces = <DeveloperWorkspace>[];
      for (final template in await repository.listModTemplates()) {
        workspaces.add(
          await repository.createModProject(
            parentDirectory: p.join(projects.path, template.id),
            id: 'test.release.${template.id}',
            name: 'Release ${template.label}',
            options: ModScaffoldOptions(
              template: template.id,
              authorName: 'TopiaForge Test',
              license: 'MIT',
            ),
          ),
        );
      }

      payload.deleteSync(recursive: true);
      for (final workspace in workspaces) {
        final project = Directory(workspace.projectRoot)
            .listSync()
            .whereType<File>()
            .singleWhere((file) => p.extension(file.path) == '.csproj');
        _expectLocalScaffoldProjectReferences(
          workspace,
          project,
          repositoryRoot.path,
        );

        final build = await Process.run(dotnet.executable, [
          'build',
          project.path,
          '-c',
          'Release',
          '--nologo',
        ], workingDirectory: workspace.projectRoot);
        expect(
          build.exitCode,
          0,
          reason:
              '${p.basename(workspace.projectRoot)}\n'
              '${build.stdout}\n${build.stderr}',
        );
        final package = await repository.packProject(workspace.projectRoot);
        expect(File(package).existsSync(), isTrue);
        final archive = ZipDecoder().decodeBytes(
          File(package).readAsBytesSync(),
          verify: true,
        );
        final manifestFile = archive.files.singleWhere(
          (file) => file.name == 'topiaforge.mod.json',
        );
        final manifest =
            jsonDecode(utf8.decode(manifestFile.content as List<int>))
                as Map<String, Object?>;
        _expectApiAssembliesPacked(archive, manifest);
        final builtWith = manifest['builtWith'] as Map<String, Object?>;
        expect(builtWith['sdkVersion'], releasePack.version);
        expect(builtWith['gameVersion'], releasePack.gameVersion);
        expect(builtWith['toolVersion'], releasePack.toolVersion);
      }
    },
    timeout: const Timeout(Duration(minutes: 3)),
  );
}
