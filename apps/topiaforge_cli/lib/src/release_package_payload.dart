import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_package_io.dart';
import 'release_package_models.dart';
import 'release_package_notices.dart';

class ReleasePackagePayloadWriter {
  const ReleasePackagePayloadWriter({
    required this.repositoryRoot,
    required this.platform,
    required this.configuration,
    required this.rebuildRuntimePayload,
    required this.fileOps,
    required this.processRunner,
    this.nugetPackagesRoot = '',
    this.distSourceRoot = '',
    this.dotnetSdkResolver = resolveRepositoryDotnetSdk,
  });

  final String repositoryRoot;
  final ReleasePackagePlatform platform;
  final String configuration;
  final bool rebuildRuntimePayload;
  final ReleaseFileOps fileOps;
  final ReleaseProcessRunner processRunner;
  final String nugetPackagesRoot;
  final String distSourceRoot;
  final RepositoryDotnetSdkResolver dotnetSdkResolver;

  Future<void> copyCommonPayload(String destinationRoot) async {
    _copyDistPayload(destinationRoot);
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'tools')),
      Directory(p.join(destinationRoot, 'tools')),
      excludedNames: _releaseToolStateNames,
      excludedNamePrefixes: const {'.env'},
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
    fileOps.copyDirectory(
      Directory(p.join(repositoryRoot, 'third_party', 'BepInEx', 'LICENSES')),
      Directory(p.join(destinationRoot, 'third_party', 'BepInEx', 'LICENSES')),
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
    _noticeWriter.copyDartCliNotices(destinationRoot);
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
      'TopiaForge.ModManager',
      'bin',
      configuration,
      'netstandard2.1',
    );
    if (!Directory(bepInEx).existsSync()) {
      throw StateError('BepInEx payload was not found at $bepInEx.');
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
      'TopiaForge.ModManager',
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
    final distSource = Directory(
      distSourceRoot.trim().isEmpty
          ? p.join(repositoryRoot, 'dist')
          : distSourceRoot,
    );
    final distDest = Directory(p.join(destinationRoot, 'dist'))
      ..createSync(recursive: true);
    if (!distSource.existsSync()) {
      return;
    }
    for (final file in listBoundedDirectorySync(distSource).whereType<File>()) {
      if (p.extension(file.path) == '.topiaforgemod') {
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
    fileOps.copyDirectory(
      source,
      destination,
      excludedNames: _generatedTemplateStateNames,
    );

    // Opening the checked-in Unity authoring template generates solution and
    // project files beside its sources. They are machine-specific caches, not
    // template inputs; do not let a maintainer's local editor state inflate or
    // contaminate a release payload. Mod-template `{{ASSEMBLY_NAME}}.csproj`
    // files live deeper in templates/mod and remain intentional sources.
    final world = Directory(
      p.join(destination.path, 'TopiaForge.UnityWorldTemplate'),
    );
    if (world.existsSync()) {
      for (final entity in listBoundedDirectorySync(world).whereType<File>()) {
        final extension = p.extension(entity.path).toLowerCase();
        if (extension == '.csproj' ||
            extension == '.sln' ||
            extension == '.user') {
          entity.deleteSync();
        }
      }
    }
  }

  Future<void> _publishGameCompatExtractor(String destinationRoot) async {
    final project = p.join(
      repositoryRoot,
      'src',
      'TopiaForge.GameCompat.Extractor',
      'TopiaForge.GameCompat.Extractor.csproj',
    );
    if (!File(project).existsSync()) {
      throw StateError('GameCompat extractor project was not found: $project');
    }
    final dotnet = await dotnetSdkResolver(Directory(repositoryRoot));
    if (platform == ReleasePackagePlatform.macos) {
      await _publishUniversalMacGameCompatExtractor(
        dotnet.executable,
        project,
        destinationRoot,
      );
      return;
    }

    final runtimeId = platform.gameCompatExtractorRuntimeIds.single;
    final publishDir = p.join(
      repositoryRoot,
      'src',
      'TopiaForge.GameCompat.Extractor',
      'bin',
      configuration,
      'publish',
      runtimeId,
    );
    stdout.writeln(
      'Publishing the GameCompat extractor ($runtimeId) into the package payload...',
    );
    await _publishGameCompatExtractorRuntime(
      dotnet.executable,
      project,
      publishDir,
    );
    final extractor = platform.gameCompatExtractorFileName;
    final published = p.join(publishDir, extractor);
    if (!File(published).existsSync()) {
      throw StateError(
        'GameCompat extractor publish did not produce $published.',
      );
    }
    fileOps.copyFileIfExists(published, p.join(destinationRoot, extractor));
    await fileOps.setExecutableBit(p.join(destinationRoot, extractor));
    _copyDotnetRuntimeNotices(destinationRoot, runtimeId);
  }

  Future<void> _publishUniversalMacGameCompatExtractor(
    String dotnetExecutable,
    String project,
    String destinationRoot,
  ) async {
    if (!await processRunner.commandExists('lipo')) {
      throw StateError(
        'lipo is required to build the universal macOS GameCompat extractor.',
      );
    }

    final inputs = <String>[];
    for (final runtimeId in platform.gameCompatExtractorRuntimeIds) {
      final publishDir = p.join(
        repositoryRoot,
        'src',
        'TopiaForge.GameCompat.Extractor',
        'bin',
        configuration,
        'publish',
        runtimeId,
      );
      stdout.writeln(
        'Publishing the GameCompat extractor ($runtimeId) into the package payload...',
      );
      await _publishGameCompatExtractorRuntime(
        dotnetExecutable,
        project,
        publishDir,
      );
      final extractorPath = p.join(
        publishDir,
        platform.gameCompatExtractorFileName,
      );
      if (!File(extractorPath).existsSync()) {
        throw StateError(
          'GameCompat extractor publish did not produce $extractorPath.',
        );
      }
      inputs.add(extractorPath);
    }

    final destination = p.join(
      destinationRoot,
      platform.gameCompatExtractorFileName,
    );
    await processRunner.runChecked('lipo', [
      '-create',
      ...inputs,
      '-output',
      destination,
    ]);
    await fileOps.setExecutableBit(destination);
    _copyDotnetRuntimeNotices(
      destinationRoot,
      platform.gameCompatExtractorRuntimeIds.first,
    );
  }

  Future<void> _publishGameCompatExtractorRuntime(
    String dotnetExecutable,
    String project,
    String publishDir,
  ) async {
    final runtimeId = p.basename(publishDir);
    await processRunner.runChecked(dotnetExecutable, [
      'publish',
      project,
      '-c',
      configuration,
      '-r',
      runtimeId,
      '--self-contained',
      'true',
      '-p:RuntimeFrameworkVersion=$_gameCompatRuntimeVersion',
      '-p:PublishSingleFile=true',
      '-o',
      publishDir,
    ], workingDirectory: repositoryRoot);
  }

  void _copyDotnetRuntimeNotices(String destinationRoot, String runtimeId) {
    final home =
        Platform.environment[Platform.isWindows ? 'USERPROFILE' : 'HOME'];
    final configuredPackages = nugetPackagesRoot.trim().isNotEmpty
        ? nugetPackagesRoot.trim()
        : (Platform.environment['NUGET_PACKAGES'] ?? '').trim();
    final packagesRoot = configuredPackages.isNotEmpty
        ? configuredPackages
        : home == null || home.trim().isEmpty
        ? ''
        : p.join(home, '.nuget', 'packages');
    if (packagesRoot.isEmpty) {
      throw StateError('The NuGet package cache root could not be located.');
    }
    final runtimePack = p.join(
      packagesRoot,
      'microsoft.netcore.app.runtime.$runtimeId',
      _gameCompatRuntimeVersion,
    );
    final license = p.join(runtimePack, 'LICENSE.TXT');
    final notices = p.join(runtimePack, 'THIRD-PARTY-NOTICES.TXT');
    if (!File(license).existsSync() || !File(notices).existsSync()) {
      throw StateError(
        '.NET runtime notices were not restored for $runtimeId '
        '$_gameCompatRuntimeVersion.',
      );
    }
    final destination = p.join(destinationRoot, 'third_party', 'dotnet');
    fileOps.copyFileIfExists(license, p.join(destination, 'LICENSE.txt'));
    fileOps.copyFileIfExists(
      notices,
      p.join(destination, 'ThirdPartyNotices.txt'),
    );
    final metadataLoadContext = p.join(
      packagesRoot,
      'system.reflection.metadataloadcontext',
      _metadataLoadContextVersion,
    );
    final metadataNotices = p.join(
      metadataLoadContext,
      'THIRD-PARTY-NOTICES.TXT',
    );
    if (!File(metadataNotices).existsSync()) {
      throw StateError(
        'System.Reflection.MetadataLoadContext notices were not restored.',
      );
    }
    final metadataLicense = _metadataLoadContextLicense(
      packageRoot: metadataLoadContext,
      dotnetMitLicense: license,
    );
    fileOps.copyFileIfExists(
      metadataLicense,
      p.join(destination, 'MetadataLoadContext-LICENSE.txt'),
    );
    fileOps.copyFileIfExists(
      metadataNotices,
      p.join(destination, 'MetadataLoadContext-ThirdPartyNotices.txt'),
    );
    File(p.join(destination, 'VERSION.txt'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('$_gameCompatRuntimeVersion\n', flush: true);
  }

  String _metadataLoadContextLicense({
    required String packageRoot,
    required String dotnetMitLicense,
  }) {
    final bundled = p.join(packageRoot, 'LICENSE.TXT');
    if (File(bundled).existsSync()) {
      return bundled;
    }

    // MetadataLoadContext 10.0.9 declares MIT in its signed NuGet manifest
    // but does not carry a separate LICENSE.TXT. Verify that declaration
    // before using the identical .NET Foundation MIT text restored with the
    // pinned runtime pack; never silently substitute a license for another
    // package or expression.
    final nuspec = File(
      p.join(packageRoot, 'system.reflection.metadataloadcontext.nuspec'),
    );
    if (!nuspec.existsSync()) {
      throw StateError(
        'System.Reflection.MetadataLoadContext license metadata was not restored.',
      );
    }
    final text = readBoundedTextFileSync(
      nuspec,
      maxBytes: CliFileLimits.metadata,
    );
    final declaresMit = RegExp(
      r'''<license\s+type=["']expression["']\s*>\s*MIT\s*</license>''',
      caseSensitive: false,
    ).hasMatch(text);
    if (!declaresMit) {
      throw StateError(
        'System.Reflection.MetadataLoadContext must declare the MIT license.',
      );
    }
    return dotnetMitLicense;
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
      'TopiaForge.ModManager',
    );
    Directory(pluginDir).createSync(recursive: true);
    for (final dll in _loaderDlls) {
      fileOps.copyFileIfExists(p.join(pluginOut, dll), p.join(pluginDir, dll));
    }
  }

  ReleasePackageNoticeWriter get _noticeWriter => ReleasePackageNoticeWriter(
    repositoryRoot: repositoryRoot,
    fileOps: fileOps,
  );
}

const _loaderDlls = [
  'TopiaForge.ModManager.dll',
  'TopiaForge.ModManager.Core.dll',
  'TopiaForge.Mods.Abstractions.dll',
  'TopiaForge.Mods.UnityUi.dll',
];

const _gameCompatRuntimeVersion = '10.0.9';
const _metadataLoadContextVersion = '10.0.9';

const _releaseToolStateNames = {
  '.dart_tool',
  '.ds_store',
  '.env',
  '.env.local',
  '.git',
  '.idea',
  '.npmrc',
  '.tools',
  '.vs',
  'build',
  'builds',
  'library',
  'logs',
  'memorycaptures',
  'node_modules',
  'obj',
  'recordings',
  'temp',
  'usersettings',
};

const _generatedTemplateStateNames = {..._releaseToolStateNames, 'bin'};
