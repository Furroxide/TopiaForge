import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory repoRoot;
  late Directory projectDir;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync(
      'multiplayer_contract_pack_test',
    );
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    File(p.join(repoRoot.path, 'global.json')).writeAsStringSync(
      jsonEncode({
        'sdk': {'version': '10.0.301', 'rollForward': 'disable'},
      }),
    );
    projectDir = Directory(p.join(root.path, 'sample.mod'))..createSync();
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  void writeManifest([Map<String, Object?>? overrides]) {
    final manifest = <String, Object?>{
      'schemaVersion': 5,
      'name': 'sample.mod',
      'displayName': 'Sample Mod',
      'version': '1.2.3',
      'author': {'name': 'Tester'},
      'entryAssembly': 'Sample.dll',
      'entryType': 'Sample.Entry',
      'supportedGameVersionRange': '*',
      'supportedLoaderVersionRange': '*',
      'supportedSdkVersionRange': '*',
      ...?overrides,
    };
    File(
      p.join(projectDir.path, 'topiaforge.mod.json'),
    ).writeAsStringSync(jsonEncode(manifest));
  }

  void writeEmptyContractLock({String? protocol = '1.0.0'}) {
    File(
      p.join(projectDir.path, 'topiaforge.multiplayer.lock.json'),
    ).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 2,
        'protocolVersion': ?protocol,
        'contracts': <Object?>[],
      }),
    );
  }

  Archive readPackage(String path) =>
      ZipDecoder().decodeBytes(File(path).readAsBytesSync());

  Map<String, Object?> packedManifest(Archive archive) =>
      jsonDecode(
            utf8.decode(
              archive.files
                      .singleWhere((file) => file.name == 'topiaforge.mod.json')
                      .content
                  as List<int>,
            ),
          )
          as Map<String, Object?>;

  String generatedContractMarker({String wireFormatRevision = '1'}) {
    const digest =
        '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';
    final payload = [
      'sample.contract',
      wireFormatRevision,
      digest,
      'sample.contract/state',
      'sample.contract/command',
      'sample.contract/object',
      'sample.contract/event',
    ].join('\n');
    return '// TopiaForge.MultiplayerContractLock:v2:'
        '${base64.encode(utf8.encode(payload))}';
  }

  Future<void> useFakeMultiplayerBuild({
    String wireFormatRevision = '1',
    bool emitContract = true,
  }) async {
    final output = File(
      p.join(projectDir.path, 'bin', 'Release', 'net10.0', 'Sample.dll'),
    );
    final fakeDotnet = File(
      p.join(
        root.path,
        Platform.isWindows ? 'multiplayer-dotnet.cmd' : 'multiplayer-dotnet',
      ),
    );
    if (Platform.isWindows) {
      const generatedOutputPrefix = '-p:CompilerGeneratedFilesOutputPath=';
      final fakePowerShell = File(p.join(root.path, 'multiplayer-dotnet.ps1'))
        ..writeAsStringSync('''
param([Parameter(Mandatory = \$true)][string]\$Generated)
[IO.Directory]::CreateDirectory(${_powerShellQuote(output.parent.path)}) | Out-Null
[IO.Directory]::CreateDirectory(\$Generated) | Out-Null
[IO.File]::WriteAllText(${_powerShellQuote(output.path)}, 'dll')
${emitContract ? "[IO.File]::WriteAllText((Join-Path \$Generated 'Contract.Multiplayer.g.cs'), ${_powerShellQuote('${generatedContractMarker(wireFormatRevision: wireFormatRevision)}\n')})" : ''}
''');
      fakeDotnet.writeAsStringSync(
        '@echo off\r\n'
        'setlocal EnableDelayedExpansion\r\n'
        'set "generated="\r\n'
        'set "captureGenerated="\r\n'
        ':parse\r\n'
        'if "%~1"=="" goto parsed\r\n'
        'set "argument=%~1"\r\n'
        'if defined captureGenerated (\r\n'
        '  set "generated=!argument!"\r\n'
        '  goto parsed\r\n'
        ')\r\n'
        'if /I "!argument!"=="-p:CompilerGeneratedFilesOutputPath" '
        'set "captureGenerated=1"\r\n'
        'if /I "!argument:~0,${generatedOutputPrefix.length}!"=='
        '"$generatedOutputPrefix" '
        'set "generated=!argument:~${generatedOutputPrefix.length}!"\r\n'
        'shift\r\n'
        'goto parse\r\n'
        ':parsed\r\n'
        'if not defined generated (\r\n'
        '  >&2 echo fake dotnet did not receive the generated-output argument: %*\r\n'
        '  exit /b 2\r\n'
        ')\r\n'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File '
        '"${fakePowerShell.path}" -Generated "!generated!"\r\n',
      );
    } else {
      fakeDotnet.writeAsStringSync('''#!/bin/sh
set -eu
generated=''
for argument in "\$@"; do
  case "\$argument" in
    -p:CompilerGeneratedFilesOutputPath=*) generated="\${argument#*=}" ;;
  esac
done
test -n "\$generated"
mkdir -p ${_shellQuote(output.parent.path)} "\$generated"
printf dll > ${_shellQuote(output.path)}
${emitContract ? 'printf \'%s\\n\' ${_shellQuote(generatedContractMarker(wireFormatRevision: wireFormatRevision))} > "\$generated/Contract.Multiplayer.g.cs"' : ':'}
''');
      final chmod = await Process.run('chmod', ['700', fakeDotnet.path]);
      expect(chmod.exitCode, 0);
    }
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
      dotnetSdkResolver: (_) async => DotnetSdkSelection(
        executable: fakeDotnet.path,
        version: '10.0.301',
        requiredVersion: '10.0.301',
      ),
    );
  }

  test('packs the contract lock as synchronized session content', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': ['Content/gameplay-rules.json'],
      },
    });
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await useFakeMultiplayerBuild(emitContract: false);
    final rules = File(
      p.join(projectDir.path, 'Content', 'gameplay-rules.json'),
    )..createSync(recursive: true);
    rules.writeAsStringSync('{"difficulty":"hard"}');
    writeEmptyContractLock();

    final archive = readPackage(
      await repository.packModDirectory(projectDir.path),
    );
    final packed = packedManifest(archive);
    final archivedLock = archive.files.singleWhere(
      (file) => file.name == 'topiaforge.multiplayer.lock.json',
    );
    final multiplayer = packed['multiplayer'] as Map<String, Object?>;
    final hashes = packed['hashes'] as Map;

    expect(multiplayer['synchronizedFiles'], [
      'Content/gameplay-rules.json',
      'topiaforge.multiplayer.lock.json',
    ]);
    expect(
      hashes['Content/gameplay-rules.json'],
      sha256.convert(rules.readAsBytesSync()).toString(),
    );
    expect(
      hashes['topiaforge.multiplayer.lock.json'],
      sha256.convert(archivedLock.content as List<int>).toString(),
    );
    final source =
        jsonDecode(
              File(
                p.join(projectDir.path, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(source, isNot(contains('hashes')));
    expect((source['multiplayer'] as Map)['synchronizedFiles'], [
      'Content/gameplay-rules.json',
    ]);
  });

  test(
    'canonicalizes an already declared lock without duplicating it',
    () async {
      writeManifest({
        'multiplayer': {
          'mode': 'session',
          'presence': 'required',
          'protocol': {'version': '1.0.0'},
          'synchronizedFiles': ['TopiaForge.Multiplayer.Lock.Json'],
        },
      });
      File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
      File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
      await useFakeMultiplayerBuild(emitContract: false);
      writeEmptyContractLock();

      final archive = readPackage(
        await repository.packModDirectory(projectDir.path),
      );
      final packed = packedManifest(archive);
      final multiplayer = packed['multiplayer'] as Map<String, Object?>;

      expect(multiplayer['synchronizedFiles'], [
        'topiaforge.multiplayer.lock.json',
      ]);
      expect((packed['hashes'] as Map), hasLength(1));
    },
  );

  test('reserves one synchronized-file slot for the generated lock', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': [
          for (var index = 0; index < 256; index++) 'Content/$index.json',
        ],
      },
    });
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    writeEmptyContractLock();

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('at most 255 synchronized files') &&
              error.toString().contains('synchronized automatically'),
        ),
      ),
    );
  });

  test('does not add admission metadata to standalone packages', () async {
    writeManifest();
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');

    final archive = readPackage(
      await repository.packModDirectory(projectDir.path),
    );
    final packed = packedManifest(archive);

    expect(packed, isNot(contains('multiplayer')));
    expect(packed, isNot(contains('hashes')));
    expect(
      archive.files.map((file) => file.name),
      isNot(contains('topiaforge.multiplayer.lock.json')),
    );
  });

  test('does not synchronize the lock for non-session modes', () async {
    writeManifest({
      'multiplayer': {'mode': 'client-local'},
    });
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await useFakeMultiplayerBuild(emitContract: false);
    writeEmptyContractLock(protocol: null);

    final archive = readPackage(
      await repository.packModDirectory(projectDir.path),
    );
    final packed = packedManifest(archive);

    expect(packed['multiplayer'], {'mode': 'client-local'});
    expect(packed, isNot(contains('hashes')));
  });

  test('sync generates and pack verifies the checked-in lock', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await useFakeMultiplayerBuild();

    final lockPath = await repository.synchronizeMultiplayerContractLock(
      projectDir.path,
    );
    final lock = jsonDecode(File(lockPath).readAsStringSync()) as Map;
    expect(lock['protocolVersion'], '1.0.0');
    expect((lock['contracts'] as List).single, {
      'id': 'sample.contract',
      'wireFormatRevision': 1,
      'schemaSha256':
          '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
      'stateIds': ['sample.contract/state'],
      'commandIds': ['sample.contract/command'],
      'objectTypeIds': ['sample.contract/object'],
      'eventIds': ['sample.contract/event'],
    });

    final archive = readPackage(
      await repository.packModDirectory(projectDir.path),
    );
    final archivedLock = archive.files.singleWhere(
      (file) => file.name == 'topiaforge.multiplayer.lock.json',
    );
    expect(jsonDecode(utf8.decode(archivedLock.content as List<int>)), lock);
  });

  test('rejects generated wire revisions outside positive Int32', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    for (final revision in ['0', '2147483648']) {
      await useFakeMultiplayerBuild(wireFormatRevision: revision);
      await expectLater(
        repository.synchronizeMultiplayerContractLock(projectDir.path),
        throwsA(
          predicate(
            (error) => error.toString().contains(
              'generator emitted invalid contract metadata',
            ),
          ),
        ),
      );
    }
  });

  test('pack rejects stale, missing, and arbitrary contract locks', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await useFakeMultiplayerBuild();

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(predicate((error) => error.toString().contains('is missing'))),
    );
    writeEmptyContractLock();
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('stale or tampered') &&
              error.toString().contains('mod sync multiplayer'),
        ),
      ),
    );
    File(
      p.join(projectDir.path, 'topiaforge.multiplayer.lock.json'),
    ).writeAsStringSync('{"schemaVersion":2,"contracts":[{"id":"guess"}]}');
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(
        predicate((error) => error.toString().contains('stale or tampered')),
      ),
    );
  });

  test('rejects a synchronized file absent from the package', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': ['Content/missing.json'],
      },
    });
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await useFakeMultiplayerBuild(emitContract: false);
    writeEmptyContractLock();

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('was not included in the package'),
        ),
      ),
    );
  });

  test('rejects source-less multiplayer synchronization and packing', () async {
    writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    writeEmptyContractLock();

    for (final operation in <Future<Object?> Function()>[
      () => repository.synchronizeMultiplayerContractLock(projectDir.path),
      () => repository.packModDirectory(projectDir.path),
    ]) {
      await expectLater(
        operation(),
        throwsA(
          predicate(
            (error) =>
                error.toString().contains('one root C# project') &&
                error.toString().contains('precompiled-only'),
          ),
        ),
      );
    }
  });
}

String _shellQuote(String value) => "'${value.replaceAll("'", "'\\''")}'";

String _powerShellQuote(String value) => "'${value.replaceAll("'", "''")}'";
