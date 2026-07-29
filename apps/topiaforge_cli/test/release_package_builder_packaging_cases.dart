part of 'release_package_builder_test.dart';

void _registerReleasePackagingTests() {
  test('builds a package from prebuilt launcher and CLI fixture', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'launcher'))
      ..createSync(recursive: true);
    File(
      p.join(launcher.path, 'topiaforge_launcher.exe'),
    ).writeAsStringSync('launcher');
    _writeFile(launcher, [
      'data',
      'flutter_assets',
      'NOTICES.Z',
    ], 'Flutter notices');
    final cli = File(p.join(temp.path, 'topiaforge.exe'))
      ..writeAsStringSync('cli');
    final output = Directory(p.join(temp.path, 'out'));

    final zipPath = await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: output.path,
      prebuiltLauncher: launcher.path,
      prebuiltCli: cli.path,
      rebuildRuntimePayload: false,
    ).build();

    await ReleasePackageValidator(
      platform: ReleasePackagePlatform.windows,
      zipPath: zipPath,
      requireRuntimePayload: false,
      runCliSmoke: false,
    ).validate();

    final extracted = Directory(p.join(temp.path, 'extracted'));
    await const ReleaseFileOps().extractPlatformZip(
      File(zipPath),
      extracted,
      ReleasePackagePlatform.windows,
    );
    expect(File(p.join(extracted.path, 'docs', 'Guide.md')).existsSync(), true);
    expect(
      Directory(p.join(extracted.path, 'docs', 'internal')).existsSync(),
      false,
    );
    expect(
      File(
        p.join(extracted.path, 'launcher', 'topiaforge_launcher.exe'),
      ).existsSync(),
      true,
    );
    expect(
      Directory(
        p.join(extracted.path, 'tools', 'sidecar', 'node_modules'),
      ).existsSync(),
      false,
    );
    expect(
      Directory(p.join(extracted.path, 'tools', 'sidecar', 'bin')).existsSync(),
      false,
    );
    expect(
      File(p.join(extracted.path, 'tools', 'sidecar', '.npmrc')).existsSync(),
      false,
    );
    expect(
      File(
        p.join(
          extracted.path,
          'third_party',
          'dart',
          'LICENSES',
          'VERSIONS.json',
        ),
      ).existsSync(),
      true,
    );
    expect(
      File(
        p.join(
          extracted.path,
          'launcher',
          'data',
          'flutter_assets',
          'NOTICES.Z',
        ),
      ).existsSync(),
      true,
    );
    final worldTemplate = p.join(
      extracted.path,
      'templates',
      'TopiaForge.UnityWorldTemplate',
    );
    expect(
      File(p.join(worldTemplate, 'Assets', 'World.prefab')).existsSync(),
      true,
    );
    for (final generated in const [
      'Library',
      'Build',
      'Logs',
      'UserSettings',
    ]) {
      expect(Directory(p.join(worldTemplate, generated)).existsSync(), false);
    }
    expect(
      File(
        p.join(worldTemplate, 'TopiaForge.WorldCompanion.Editor.csproj'),
      ).existsSync(),
      false,
    );
    expect(
      File(
        p.join(worldTemplate, 'TopiaForge.UnityWorldTemplate.sln'),
      ).existsSync(),
      false,
    );
  });

  test('builds and validates the Linux release layout', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'linux-launcher'))
      ..createSync(recursive: true);
    _writeFile(launcher, ['topiaforge_launcher'], 'launcher');
    _writeFile(launcher, [
      'data',
      'flutter_assets',
      'NOTICES.Z',
    ], 'Flutter notices');
    final cli = File(p.join(temp.path, 'topiaforge'))..writeAsStringSync('cli');

    final zipPath = await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.linux,
      outputRoot: p.join(temp.path, 'linux-out'),
      prebuiltLauncher: launcher.path,
      prebuiltCli: cli.path,
      rebuildRuntimePayload: false,
    ).build();

    await ReleasePackageValidator(
      platform: ReleasePackagePlatform.linux,
      zipPath: zipPath,
      requireRuntimePayload: false,
      runCliSmoke: false,
    ).validate();

    final extracted = Directory(p.join(temp.path, 'linux-extracted'));
    await const ReleaseFileOps().extractPlatformZip(
      File(zipPath),
      extracted,
      ReleasePackagePlatform.linux,
    );
    expect(File(p.join(extracted.path, 'topiaforge')).existsSync(), true);
    expect(
      File(
        p.join(extracted.path, 'launcher', 'topiaforge_launcher'),
      ).existsSync(),
      true,
    );
  });

  test('nested CLI builds use the current pinned Dart VM', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'launcher'))
      ..createSync(recursive: true);
    File(
      p.join(launcher.path, 'topiaforge_launcher.exe'),
    ).writeAsStringSync('launcher');
    _writeFile(launcher, [
      'data',
      'flutter_assets',
      'NOTICES.Z',
    ], 'Flutter notices');
    final runner = _RecordingProcessRunner(
      onRun: (call) async {
        if (call.executable == Platform.resolvedExecutable &&
            call.arguments.contains('compile')) {
          final output = _argAfter(call.arguments, '-o');
          File(output)
            ..parent.createSync(recursive: true)
            ..writeAsStringSync('compiled cli');
        }
      },
    );

    await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: p.join(temp.path, 'out'),
      prebuiltLauncher: launcher.path,
      rebuildRuntimePayload: false,
      processRunner: runner,
    ).build();

    final dartCalls = runner.calls
        .where((call) => call.executable == Platform.resolvedExecutable)
        .toList();
    expect(dartCalls, hasLength(2));
    expect(dartCalls.first.arguments, ['pub', 'get', '--enforce-lockfile']);
    expect(dartCalls.last.arguments, containsAllInOrder(['compile', 'exe']));
  });

  test('validator rejects a package without launcher output', () async {
    final zip = File(p.join(temp.path, 'TopiaForge-windows-x64.zip'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('tools/readme.txt', 'tools'))
      ..addFile(ArchiveFile.string('templates/readme.txt', 'templates'))
      ..addFile(ArchiveFile.string('docs/readme.txt', 'docs'))
      ..addFile(ArchiveFile.string('bindings/readme.txt', 'bindings'))
      ..addFile(ArchiveFile.string('baselines/readme.txt', 'baselines'))
      ..addFile(ArchiveFile.string('THIRD_PARTY_NOTICES.md', 'notices'))
      ..addFile(ArchiveFile.string('dist/vpm/index.json', '{}'))
      ..addFile(ArchiveFile.string('dist/test.topiaforgemod', 'pkg'))
      ..addFile(ArchiveFile.string('topiaforge.exe', 'cli'));
    zip.writeAsBytesSync(ZipEncoder().encode(archive));

    expect(
      () => ReleasePackageValidator(
        platform: ReleasePackagePlatform.windows,
        zipPath: zip.path,
        requireRuntimePayload: false,
        runCliSmoke: false,
      ).validate(),
      throwsA(isA<StateError>()),
    );
  });

  test('validator does not execute an embedded CLI unless opted in', () async {
    final marker = File(p.join(temp.path, 'embedded-cli-ran'));
    final cli = ArchiveFile.string(
      'topiaforge.exe',
      '#!/bin/sh\ntouch "${marker.path}"\necho "TopiaForge CLI"\n',
    )..mode = 0x1ed;
    final zip = File(p.join(temp.path, 'untrusted-package.zip'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('tools/readme.txt', 'tools'))
      ..addFile(ArchiveFile.string('templates/readme.txt', 'templates'))
      ..addFile(ArchiveFile.string('docs/readme.txt', 'docs'))
      ..addFile(ArchiveFile.string('bindings/readme.txt', 'bindings'))
      ..addFile(ArchiveFile.string('baselines/readme.txt', 'baselines'))
      ..addFile(ArchiveFile.string('THIRD_PARTY_NOTICES.md', 'notices'))
      ..addFile(ArchiveFile.string('dist/vpm/index.json', '{}'))
      ..addFile(cli);
    for (final license in _bepInExLicenseNames) {
      archive.addFile(
        ArchiveFile.string('third_party/BepInEx/LICENSES/$license', 'license'),
      );
    }
    _addDartNotices(archive);
    _addSdkPayload(archive);
    zip.writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));

    await ReleasePackageValidator(
      platform: ReleasePackagePlatform.windows,
      zipPath: zip.path,
      requireRuntimePayload: false,
      requireLauncher: false,
      requireDistPackages: false,
    ).validate();

    expect(marker.existsSync(), false);
  });

  test('macOS validator checks every nested Mach-O architecture', () async {
    const payload = 'TopiaForge.app/Contents/Resources/TopiaForge';
    final dispatcher = ArchiveFile.string(
      '$payload/topiaforge',
      '#!/bin/sh\nexit 0\n',
    )..mode = 0x1ed;
    final archive = Archive()
      ..addFile(dispatcher)
      ..addFile(_machOFile('$payload/$macCliArm64FileName'))
      ..addFile(_machOFile('$payload/$macCliX64FileName'))
      ..addFile(_machOFile('TopiaForge.app/Contents/MacOS/topiaforge_launcher'))
      ..addFile(
        _machOFile(
          'TopiaForge.app/Contents/Frameworks/Nested.framework/Versions/A/Nested',
        ),
      )
      ..addFile(
        ArchiveFile.string('topiaforge', '#!/bin/sh\nexit 0\n')..mode = 0x1ed,
      )
      ..addFile(ArchiveFile.string('$payload/tools/readme.txt', 'tools'))
      ..addFile(
        ArchiveFile.string('$payload/templates/readme.txt', 'templates'),
      )
      ..addFile(ArchiveFile.string('$payload/docs/readme.txt', 'docs'))
      ..addFile(ArchiveFile.string('$payload/bindings/readme.txt', 'bindings'))
      ..addFile(
        ArchiveFile.string('$payload/baselines/readme.txt', 'baselines'),
      )
      ..addFile(
        ArchiveFile.string('$payload/THIRD_PARTY_NOTICES.md', 'notices'),
      )
      ..addFile(
        ArchiveFile.string(
          'TopiaForge.app/Contents/Frameworks/App.framework/Resources/'
              'flutter_assets/NOTICES.Z',
          'Flutter notices',
        ),
      )
      ..addFile(ArchiveFile.string('$payload/dist/vpm/index.json', '{}'));
    for (final license in _bepInExLicenseNames) {
      archive.addFile(
        ArchiveFile.string(
          '$payload/third_party/BepInEx/LICENSES/$license',
          'license',
        ),
      );
    }
    _addDartNotices(archive, prefix: payload);
    _addSdkPayload(archive, prefix: payload);
    final zip = File(p.join(temp.path, 'macos-nested-arch.zip'))
      ..writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));
    final runner = _RecordingProcessRunner(
      onResult: (call) {
        if (call.executable != 'lipo') {
          return ProcessResult(1, 0, '', '');
        }
        final path = call.arguments.last;
        final name = p.basename(path);
        final architectures = name == macCliArm64FileName
            ? 'arm64'
            : name == macCliX64FileName
            ? 'x86_64'
            : name == 'Nested'
            ? 'arm64'
            : 'x86_64 arm64';
        return ProcessResult(1, 0, architectures, '');
      },
    );

    await expectLater(
      () => ReleasePackageValidator(
        platform: ReleasePackagePlatform.macos,
        zipPath: zip.path,
        requireMacUniversal: true,
        requireRuntimePayload: false,
        requireDistPackages: false,
        processRunner: runner,
      ).validate(),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          allOf(contains('Nested.framework'), contains('not universal')),
        ),
      ),
    );
  });

  test('GameCompat publish failures abort release package assembly', () async {
    final repo = _writeFixtureRepo(temp);
    final destination = Directory(p.join(temp.path, 'payload'))
      ..createSync(recursive: true);
    final runner = _RecordingProcessRunner(
      onRun: (call) async {
        if (call.executable == _fixtureDotnetExecutable) {
          throw StateError('publish failed');
        }
      },
    );

    await expectLater(
      ReleasePackagePayloadWriter(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.windows,
        configuration: 'Release',
        rebuildRuntimePayload: true,
        fileOps: ReleaseFileOps(processRunner: runner),
        processRunner: runner,
        dotnetSdkResolver: _resolveFixtureDotnetSdk,
      ).copyCommonPayload(destination.path),
      throwsA(isA<StateError>()),
    );
  });

  test('macOS launcher packaging only accepts TopiaForge.app', () async {
    final repo = _writeFixtureRepo(temp);
    final wrongApp = Directory(p.join(temp.path, 'WrongName.app'))
      ..createSync(recursive: true);
    final output = Directory(p.join(temp.path, 'out'));

    await expectLater(
      () => ReleasePackageBuilder(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.macos,
        outputRoot: output.path,
        prebuiltLauncher: wrongApp.path,
        rebuildRuntimePayload: false,
      ).build(),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          contains('TopiaForge.app'),
        ),
      ),
    );
  });

  test(
    'macOS GameCompat extractor publish creates a universal binary',
    () async {
      final repo = _writeFixtureRepo(temp);
      final destination = Directory(p.join(temp.path, 'payload'))
        ..createSync(recursive: true);
      late final _RecordingProcessRunner runner;
      runner = _RecordingProcessRunner(
        availableCommands: {'lipo'},
        onRun: (call) async {
          if (call.executable == _fixtureDotnetExecutable &&
              call.arguments.contains('publish')) {
            final runtimeId = _argAfter(call.arguments, '-r');
            final output = _argAfter(call.arguments, '-o');
            File(p.join(output, 'TopiaForge.GameCompat.Extractor'))
              ..parent.createSync(recursive: true)
              ..writeAsStringSync(runtimeId);
          }
          if (call.executable == 'lipo') {
            final output = _argAfter(call.arguments, '-output');
            File(output)
              ..parent.createSync(recursive: true)
              ..writeAsStringSync('universal');
          }
        },
      );

      await ReleasePackagePayloadWriter(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.macos,
        configuration: 'Release',
        rebuildRuntimePayload: true,
        fileOps: ReleaseFileOps(processRunner: runner),
        processRunner: runner,
        nugetPackagesRoot: p.join(repo.path, 'nuget'),
        dotnetSdkResolver: _resolveFixtureDotnetSdk,
      ).copyCommonPayload(destination.path);

      final publishRuntimes = runner.calls
          .where((call) => call.executable == _fixtureDotnetExecutable)
          .map((call) => _argAfter(call.arguments, '-r'))
          .toList();
      expect(publishRuntimes, ['osx-x64', 'osx-arm64']);
      for (final call in runner.calls.where(
        (call) => call.executable == _fixtureDotnetExecutable,
      )) {
        expect(call.arguments, contains('-p:RuntimeFrameworkVersion=10.0.9'));
      }

      final lipo = runner.calls.singleWhere(
        (call) => call.executable == 'lipo',
      );
      expect(lipo.arguments.first, '-create');
      expect(lipo.arguments, contains('-output'));
      expect(
        File(
          p.join(destination.path, 'TopiaForge.GameCompat.Extractor'),
        ).readAsStringSync(),
        'universal',
      );
      expect(
        File(
          p.join(destination.path, 'third_party', 'dotnet', 'LICENSE.txt'),
        ).existsSync(),
        true,
      );
      expect(
        File(
          p.join(
            destination.path,
            'third_party',
            'dotnet',
            'MetadataLoadContext-LICENSE.txt',
          ),
        ).readAsStringSync(),
        '.NET license',
      );
      expect(
        File(
          p.join(
            destination.path,
            'third_party',
            'dotnet',
            'MetadataLoadContext-ThirdPartyNotices.txt',
          ),
        ).readAsStringSync(),
        'MetadataLoadContext notices',
      );
    },
  );
}
