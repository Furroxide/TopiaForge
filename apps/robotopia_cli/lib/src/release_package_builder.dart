import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';
import 'release_package_macos.dart';
import 'release_package_models.dart';
import 'release_package_payload.dart';

class ReleasePackageBuilder {
  ReleasePackageBuilder({
    required this.repositoryRoot,
    required this.platform,
    required this.outputRoot,
    this.configuration = 'Release',
    this.prebuiltLauncher = '',
    this.prebuiltCli = '',
    this.rebuildRuntimePayload = true,
    this.processRunner = const ReleaseProcessRunner(),
  }) : fileOps = ReleaseFileOps(processRunner: processRunner);

  final String repositoryRoot;
  final ReleasePackagePlatform platform;
  final String outputRoot;
  final String configuration;
  final String prebuiltLauncher;
  final String prebuiltCli;
  final bool rebuildRuntimePayload;
  final ReleaseProcessRunner processRunner;
  final ReleaseFileOps fileOps;

  Future<String> build() async {
    final output = Directory(outputRoot)..createSync(recursive: true);
    final assetName = platform.archiveName;
    final stageRoot = Directory(
      p.join(output.path, p.basenameWithoutExtension(assetName)),
    );
    final zipPath = File(p.join(output.path, assetName));

    fileOps.deleteIfExists(stageRoot.path);
    stageRoot.createSync(recursive: true);

    if (rebuildRuntimePayload) {
      await _rebuildRuntimePayload();
    } else {
      stderr.writeln(
        releaseWarning(
          'Skipping runtime/mod rebuild; copying existing dist payloads when present.',
        ),
      );
    }

    await _buildLauncher(stageRoot);
    if (platform == ReleasePackagePlatform.macos) {
      await _finishMacPackage(stageRoot);
    } else {
      await _finishFlatPackage(stageRoot);
    }

    await fileOps.writePlatformZip(stageRoot, zipPath, platform);
    stdout.writeln('Created ${zipPath.path}');
    return zipPath.path;
  }

  Future<void> _rebuildRuntimePayload() async {
    await processRunner.runChecked('dotnet', [
      'build',
      p.join(repositoryRoot, 'RobotopiaModManager.slnx'),
      '-c',
      configuration,
    ], workingDirectory: repositoryRoot);

    final cliApp = p.join(repositoryRoot, 'apps', 'robotopia_cli');
    await processRunner.runChecked('dart', [
      'pub',
      'get',
    ], workingDirectory: cliApp);
    await processRunner.runChecked('dart', [
      'run',
      p.join('bin', 'robotopia.dart'),
      'pack',
      '--all',
      '--output',
      p.join(repositoryRoot, 'dist'),
      '--configuration',
      configuration,
    ], workingDirectory: cliApp);
    await processRunner.runChecked('dart', [
      'run',
      p.join('bin', 'robotopia.dart'),
      'unity',
      'pack-packages',
      '--output',
      p.join(repositoryRoot, 'dist', 'vpm'),
    ], workingDirectory: cliApp);
  }

  Future<void> _finishFlatPackage(Directory stageRoot) async {
    await _buildCli(stageRoot.path);
    await _payloadWriter.copyCommonPayload(stageRoot.path);
    if (rebuildRuntimePayload) {
      await _payloadWriter.copyLoaderRuntime(stageRoot.path);
    }
  }

  Future<void> _finishMacPackage(Directory stageRoot) async {
    final appBundle = _locateMacApp(stageRoot.path);
    if (appBundle == null) {
      throw StateError(
        'macOS package requires a QuantumWorks.app bundle. '
        'Build failed or provide --prebuilt-launcher.',
      );
    }
    final payloadRoot = p.join(
      appBundle,
      'Contents',
      'Resources',
      'QuantumWorks',
    );
    Directory(payloadRoot).createSync(recursive: true);
    await _buildCli(payloadRoot);
    await _payloadWriter.copyCommonPayload(payloadRoot);
    if (rebuildRuntimePayload) {
      await _payloadWriter.copyLoaderRuntime(payloadRoot);
    }
    await MacPackageSigner(
      processRunner: processRunner,
    ).signIfConfigured(appBundle, stageRoot.path);
    await _writeMacCliShim(stageRoot.path);
  }

  Future<void> _buildLauncher(Directory stageRoot) async {
    if (prebuiltLauncher.trim().isNotEmpty) {
      await _copyPrebuiltLauncher(stageRoot);
      return;
    }
    if (!await processRunner.commandExists('flutter')) {
      stderr.writeln(
        releaseWarning(
          'Flutter not found on PATH; skipping launcher GUI build.',
        ),
      );
      return;
    }

    final launcherApp = p.join(
      repositoryRoot,
      'apps',
      'robotopia_launcher_flutter',
    );
    await processRunner.runChecked('flutter', [
      'build',
      platform.id,
      '--release',
    ], workingDirectory: launcherApp);
    await _copyBuiltLauncher(launcherApp, stageRoot);
  }

  Future<void> _copyPrebuiltLauncher(Directory stageRoot) async {
    final source = Directory(prebuiltLauncher);
    if (!source.existsSync()) {
      throw StateError('Prebuilt launcher was not found: $prebuiltLauncher');
    }
    if (platform == ReleasePackagePlatform.macos) {
      final appBundle = _findAppBundle(source.path);
      if (appBundle == null) {
        throw StateError(
          'Prebuilt macOS launcher must be a .app bundle or contain one: ${source.path}',
        );
      }
      await fileOps.copyMacBundle(
        appBundle,
        p.join(stageRoot.path, p.basename(appBundle)),
      );
      return;
    }
    fileOps.copyDirectoryContents(
      source,
      Directory(p.join(stageRoot.path, 'launcher')),
    );
    if (platform == ReleasePackagePlatform.linux) {
      await fileOps.setExecutableBit(
        p.join(stageRoot.path, 'launcher', 'robotopia_launcher_flutter'),
      );
    }
  }

  Future<void> _copyBuiltLauncher(
    String launcherApp,
    Directory stageRoot,
  ) async {
    if (platform == ReleasePackagePlatform.windows) {
      final releaseDir = _firstExistingDirectory([
        p.join(launcherApp, 'build', 'windows', 'x64', 'runner', 'Release'),
        p.join(launcherApp, 'build', 'windows', 'runner', 'Release'),
      ]);
      if (releaseDir == null) {
        throw StateError(
          'Could not locate the Flutter Windows Release output.',
        );
      }
      fileOps.copyDirectoryContents(
        Directory(releaseDir),
        Directory(p.join(stageRoot.path, 'launcher')),
      );
      return;
    }
    if (platform == ReleasePackagePlatform.linux) {
      final releaseDir = _firstExistingDirectory([
        p.join(launcherApp, 'build', 'linux', 'x64', 'release', 'bundle'),
        p.join(launcherApp, 'build', 'linux', 'release', 'bundle'),
      ]);
      if (releaseDir == null) {
        throw StateError('Could not locate the Flutter Linux release bundle.');
      }
      final destination = Directory(p.join(stageRoot.path, 'launcher'));
      fileOps.copyDirectoryContents(Directory(releaseDir), destination);
      await fileOps.setExecutableBit(
        p.join(destination.path, 'robotopia_launcher_flutter'),
      );
      return;
    }
    final appBundle = _findAppBundle(
      p.join(launcherApp, 'build', 'macos', 'Build', 'Products', 'Release'),
    );
    if (appBundle == null) {
      throw StateError('Could not locate the Flutter macOS app bundle.');
    }
    await fileOps.copyMacBundle(
      appBundle,
      p.join(stageRoot.path, p.basename(appBundle)),
    );
  }

  Future<void> _buildCli(String destinationRoot) async {
    final destination = p.join(destinationRoot, platform.cliFileName);
    if (prebuiltCli.trim().isNotEmpty) {
      if (!File(prebuiltCli).existsSync()) {
        throw StateError('Prebuilt CLI was not found: $prebuiltCli');
      }
      File(prebuiltCli).copySync(destination);
      await fileOps.setExecutableBit(destination);
      return;
    }

    final cliApp = p.join(repositoryRoot, 'apps', 'robotopia_cli');
    await processRunner.runChecked('dart', [
      'pub',
      'get',
    ], workingDirectory: cliApp);
    await processRunner.runChecked('dart', [
      'compile',
      'exe',
      p.join('bin', 'robotopia.dart'),
      '-o',
      destination,
    ], workingDirectory: cliApp);
    await fileOps.setExecutableBit(destination);
  }

  Future<void> _writeMacCliShim(String stageRoot) async {
    final shim = File(p.join(stageRoot, 'robotopia'));
    shim.writeAsStringSync('''
#!/bin/sh
set -eu
DIR="\$(CDPATH= cd -- "\$(dirname -- "\$0")" && pwd)"
exec "\$DIR/QuantumWorks.app/Contents/Resources/QuantumWorks/robotopia" "\$@"
''');
    await fileOps.setExecutableBit(shim.path);
  }

  String? _locateMacApp(String stageRoot) => _findAppBundle(stageRoot);

  String? _findAppBundle(String root) {
    final asFile = Directory(root);
    if (root.endsWith('.app') && asFile.existsSync()) {
      return root;
    }
    final preferred = Directory(p.join(root, 'QuantumWorks.app'));
    if (preferred.existsSync()) {
      return preferred.path;
    }
    if (!Directory(root).existsSync()) {
      return null;
    }
    for (final dir in Directory(root).listSync().whereType<Directory>()) {
      if (dir.path.endsWith('.app')) {
        return dir.path;
      }
    }
    return null;
  }

  String? _firstExistingDirectory(List<String> candidates) {
    for (final candidate in candidates) {
      if (Directory(candidate).existsSync()) {
        return candidate;
      }
    }
    return null;
  }

  ReleasePackagePayloadWriter get _payloadWriter => ReleasePackagePayloadWriter(
    repositoryRoot: repositoryRoot,
    platform: platform,
    configuration: configuration,
    rebuildRuntimePayload: rebuildRuntimePayload,
    fileOps: fileOps,
    processRunner: processRunner,
  );
}
