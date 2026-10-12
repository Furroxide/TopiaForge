import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// A temporary mod project plus a fake `dotnet` that emits generated contract
/// markers, shared by the multiplayer contract pack tests.
final class MultiplayerContractPackFixture {
  MultiplayerContractPackFixture._(
    this.root,
    this.repoRoot,
    this.projectDir,
    this.repository,
  );

  factory MultiplayerContractPackFixture.create() {
    final root = Directory.systemTemp.createTempSync(
      'multiplayer_contract_pack_test',
    );
    final repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    File(p.join(repoRoot.path, 'global.json')).writeAsStringSync(
      jsonEncode({
        'sdk': {'version': '10.0.301', 'rollForward': 'disable'},
      }),
    );
    final projectDir = Directory(p.join(root.path, 'sample.mod'))..createSync();
    return MultiplayerContractPackFixture._(
      root,
      repoRoot,
      projectDir,
      LocalDeveloperRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: repoRoot.path,
      ),
    );
  }

  final Directory root;
  final Directory repoRoot;
  final Directory projectDir;
  LocalDeveloperRepository repository;

  void dispose() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  }

  void writeManifest([Map<String, Object?>? overrides]) {
    final manifest = <String, Object?>{
      'schemaVersion': 6,
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

  void writeEmptyContractLock() {
    File(
      p.join(projectDir.path, 'topiaforge.multiplayer.lock.json'),
    ).writeAsStringSync(
      jsonEncode({'schemaVersion': 3, 'contracts': <Object?>[]}),
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
}

String _shellQuote(String value) => "'${value.replaceAll("'", "'\\''")}'";

String _powerShellQuote(String value) => "'${value.replaceAll("'", "''")}'";
