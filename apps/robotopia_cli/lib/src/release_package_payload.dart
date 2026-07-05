import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';
import 'release_package_models.dart';

class ReleasePackagePayloadWriter {
  const ReleasePackagePayloadWriter({
    required this.repositoryRoot,
    required this.platform,
    required this.configuration,
    required this.rebuildRuntimePayload,
    required this.fileOps,
    required this.processRunner,
  });

  final String repositoryRoot;
  final ReleasePackagePlatform platform;
  final String configuration;
  final bool rebuildRuntimePayload;
  final ReleaseFileOps fileOps;
  final ReleaseProcessRunner processRunner;

  Future<void> copyCommonPayload(String destinationRoot) async {
    _copyDistPayload(destinationRoot);
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'tools')),
      Directory(p.join(destinationRoot, 'tools')),
    );
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'docs')),
      Directory(p.join(destinationRoot, 'docs')),
    );
    fileOps.deleteIfExists(p.join(destinationRoot, 'docs', 'internal'));
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'bindings')),
      Directory(p.join(destinationRoot, 'bindings')),
    );
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'baselines')),
      Directory(p.join(destinationRoot, 'baselines')),
    );
    _copyTemplates(destinationRoot);
    fileOps.copyFileIfExists(
      p.join(repositoryRoot, 'README.md'),
      p.join(destinationRoot, 'README.md'),
    );
    fileOps.copyFileIfExists(
      p.join(repositoryRoot, 'THIRD_PARTY_NOTICES.md'),
      p.join(destinationRoot, 'THIRD_PARTY_NOTICES.md'),
    );
    if (rebuildRuntimePayload) {
      await _publishGameCompatExtractor(destinationRoot);
    }
  }

  Future<void> copyLoaderRuntime(String destinationRoot) async {
    final bepInEx = p.join(
      repositoryRoot,
      'third_party',
      'BepInEx',
      platform.bepInExBundleName,
    );
    final pluginOut = p.join(
      repositoryRoot,
      'src',
      'Robotopia.ModManager',
      'bin',
      configuration,
      'netstandard2.1',
    );
    if (!Directory(bepInEx).existsSync()) {
      stderr.writeln(
        releaseWarning('BepInEx payload was not found at $bepInEx.'),
      );
      return;
    }

    final bundleDest = p.join(
      destinationRoot,
      'third_party',
      'BepInEx',
      platform.bepInExBundleName,
    );
    fileOps.copyDirectory(Directory(bepInEx), Directory(bundleDest));
    if (platform == ReleasePackagePlatform.macos) {
      await fileOps.setExecutableBit(p.join(bundleDest, 'run_bepinex.sh'));
      await fileOps.setExecutableBit(p.join(bundleDest, 'libdoorstop.dylib'));
    }

    final loaderDest = p.join(
      destinationRoot,
      'src',
      'Robotopia.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    );
    Directory(loaderDest).createSync(recursive: true);
    for (final dll in _loaderDlls) {
      fileOps.copyFileIfExists(p.join(pluginOut, dll), p.join(loaderDest, dll));
    }

    if (platform == ReleasePackagePlatform.windows) {
      _copyWindowsOverlayRuntime(destinationRoot, bepInEx, pluginOut);
    }
  }

  void _copyDistPayload(String destinationRoot) {
    final distSource = Directory(p.join(repositoryRoot, 'dist'));
    final distDest = Directory(p.join(destinationRoot, 'dist'))
      ..createSync(recursive: true);
    if (!distSource.existsSync()) {
      return;
    }
    for (final file in distSource.listSync().whereType<File>()) {
      if (p.extension(file.path) == '.robotopiamod') {
        file.copySync(p.join(distDest.path, p.basename(file.path)));
      }
    }
    fileOps.copyDirectory(
      Directory(p.join(distSource.path, 'vpm')),
      Directory(p.join(distDest.path, 'vpm')),
    );
  }

  void _copyTemplates(String destinationRoot) {
    final source = Directory(p.join(repositoryRoot, 'templates'));
    final destination = Directory(p.join(destinationRoot, 'templates'));
    if (!source.existsSync()) {
      return;
    }
    destination.createSync(recursive: true);
    for (final file in source.listSync(recursive: true).whereType<File>()) {
      final relative = p.relative(file.path, from: source.path);
      final segments = p.split(relative);
      if (segments.contains('bin') || segments.contains('obj')) {
        continue;
      }
      final target = p.join(destination.path, relative);
      File(target).parent.createSync(recursive: true);
      file.copySync(target);
    }
  }

  Future<void> _publishGameCompatExtractor(String destinationRoot) async {
    final project = p.join(
      repositoryRoot,
      'src',
      'Robotopia.GameCompat.Extractor',
      'Robotopia.GameCompat.Extractor.csproj',
    );
    if (!File(project).existsSync()) {
      return;
    }
    final publishDir = p.join(
      repositoryRoot,
      'src',
      'Robotopia.GameCompat.Extractor',
      'bin',
      configuration,
      'publish',
      platform.dotnetRuntimeId,
    );
    stdout.writeln(
      'Publishing the GameCompat extractor (${platform.dotnetRuntimeId}) into the package payload...',
    );
    try {
      await processRunner.runChecked('dotnet', [
        'publish',
        project,
        '-c',
        configuration,
        '-r',
        platform.dotnetRuntimeId,
        '--self-contained',
        'true',
        '-p:PublishSingleFile=true',
        '-o',
        publishDir,
      ], workingDirectory: repositoryRoot);
      final extractor = platform.gameCompatExtractorFileName;
      fileOps.copyFileIfExists(
        p.join(publishDir, extractor),
        p.join(destinationRoot, extractor),
      );
      await fileOps.setExecutableBit(p.join(destinationRoot, extractor));
    } on Object catch (error) {
      stderr.writeln(
        releaseWarning(
          "GameCompat extractor publish failed; the launcher will report compatibility as 'unknown'. $error",
        ),
      );
    }
  }

  void _copyWindowsOverlayRuntime(
    String destinationRoot,
    String bepInEx,
    String pluginOut,
  ) {
    fileOps.copyFileIfExists(
      p.join(bepInEx, '.doorstop_version'),
      p.join(destinationRoot, '.doorstop_version'),
    );
    fileOps.copyFileIfExists(
      p.join(bepInEx, 'doorstop_config.ini'),
      p.join(destinationRoot, 'doorstop_config.ini'),
    );
    fileOps.copyFileIfExists(
      p.join(bepInEx, 'winhttp.dll'),
      p.join(destinationRoot, 'winhttp.dll'),
    );
    fileOps.copyDirectory(
      Directory(p.join(bepInEx, 'BepInEx')),
      Directory(p.join(destinationRoot, 'BepInEx')),
    );
    final pluginDir = p.join(
      destinationRoot,
      'BepInEx',
      'plugins',
      'RobotopiaModManager',
    );
    Directory(pluginDir).createSync(recursive: true);
    for (final dll in _loaderDlls) {
      fileOps.copyFileIfExists(p.join(pluginOut, dll), p.join(pluginDir, dll));
    }
  }
}

const _loaderDlls = [
  'Robotopia.ModManager.dll',
  'Robotopia.ModManager.Core.dll',
  'Robotopia.Mods.Abstractions.dll',
  'Robotopia.Mods.UnityUi.dll',
];
