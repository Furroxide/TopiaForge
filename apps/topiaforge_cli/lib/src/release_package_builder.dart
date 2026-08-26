import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'release_package_io.dart';
import 'release_package_macos.dart';
import 'release_package_models.dart';
import 'release_package_payload.dart';
import 'release_package_windows.dart';
import 'release_policy.dart';
import 'release_ecosystem_payload.dart';

class ReleasePackageBuilder {
  ReleasePackageBuilder({
    required this.repositoryRoot,
    required this.platform,
    required this.outputRoot,
    this.configuration = 'Release',
    this.prebuiltLauncher = '',
    this.prebuiltCli = '',
    this.prebuiltDist = '',
    this.rebuildRuntimePayload = true,
    this.requireMacSigning = false,
    this.requireWindowsSigning = false,
    this.isAotExecutable = _isAotExecutable,
    this.processRunner = const ReleaseProcessRunner(),
    this.dotnetSdkResolver = resolveRepositoryDotnetSdk,
  }) : fileOps = ReleaseFileOps(processRunner: processRunner);

  final String repositoryRoot;
  final ReleasePackagePlatform platform;
  final String outputRoot;
  final String configuration;
  final String prebuiltLauncher;
  final String prebuiltCli;
  final String prebuiltDist;
  final bool rebuildRuntimePayload;
  final bool requireMacSigning;
  final bool requireWindowsSigning;
  final bool isAotExecutable;
  final ReleaseProcessRunner processRunner;
  final RepositoryDotnetSdkResolver dotnetSdkResolver;
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
      await const BepInExProvenanceVerifier().verify(repositoryRoot);
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
    final dotnet = await dotnetSdkResolver(Directory(repositoryRoot));
    await processRunner.runChecked(dotnet.executable, [
      'build',
      p.join(repositoryRoot, 'TopiaForge.slnx'),
      '-c',
      configuration,
    ], workingDirectory: repositoryRoot);

    if (prebuiltDist.trim().isNotEmpty) {
      const PrebuiltEcosystemPayload().validate(
        repositoryRoot: repositoryRoot,
        path: prebuiltDist,
      );
      return;
    }

    final cliApp = p.join(repositoryRoot, 'apps', 'topiaforge_cli');
    await _runDart([
      'pub',
      'get',
      '--enforce-lockfile',
    ], workingDirectory: cliApp);
    await _runDart([
      'run',
      p.join('bin', 'topiaforge.dart'),
      'pack',
      '--all',
      '--output',
      p.join(repositoryRoot, 'dist'),
      '--configuration',
      configuration,
    ], workingDirectory: cliApp);
    // Normal bulk packing deliberately omits every DevTool, so UiGallery stays a
    // source-only QA surface.
    await _runDart([
      'run',
      p.join('bin', 'topiaforge.dart'),
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
    if (platform == ReleasePackagePlatform.windows) {
      final expectedSigner = requireWindowsSigning
          ? TopiaForgeReleasePolicy.load(
              repositoryRoot,
            ).windowsCertificateSha256
          : '';
      await WindowsPackageSigner(
        processRunner: processRunner,
        requireTrustedSignature: requireWindowsSigning,
        expectedSignerCertificateSha256: expectedSigner,
      ).signIfConfigured(stageRoot.path);
    }
  }

  Future<void> _finishMacPackage(Directory stageRoot) async {
    final appBundle = _locateMacApp(stageRoot.path);
    if (appBundle == null) {
      throw StateError(
        'macOS package requires a TopiaForge.app bundle. '
        'Build failed or provide --prebuilt-launcher.',
      );
    }
    final payloadRoot = p.join(
      appBundle,
      'Contents',
      'Resources',
      'TopiaForge',
    );
    Directory(payloadRoot).createSync(recursive: true);
    await _buildCli(payloadRoot);
    await _payloadWriter.copyCommonPayload(payloadRoot);
    if (rebuildRuntimePayload) {
      await _payloadWriter.copyLoaderRuntime(payloadRoot);
    }
    await MacPackageSigner(
      processRunner: processRunner,
      requireTrustedSignature: requireMacSigning,
    ).signIfConfigured(appBundle, stageRoot.path);
    await _writeMacCliEntrypoint(stageRoot.path);
  }

  Future<void> _buildLauncher(Directory stageRoot) async {
    if (prebuiltLauncher.trim().isNotEmpty) {
      await _copyPrebuiltLauncher(stageRoot);
      return;
    }
    final flutter = await _resolveFlutterCommand();

    final launcherApp = p.join(
      repositoryRoot,
      'apps',
      'topiaforge_launcher_flutter',
    );
    await processRunner.runChecked(flutter, [
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
          'Prebuilt macOS launcher must be TopiaForge.app or contain it: ${source.path}',
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
        p.join(stageRoot.path, 'launcher', 'topiaforge_launcher'),
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
        p.join(destination.path, 'topiaforge_launcher'),
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
    if (platform == ReleasePackagePlatform.macos) {
      if (prebuiltCli.trim().isEmpty) {
        throw StateError(
          'A universal macOS release requires --prebuilt-cli to point to a '
          'directory containing topiaforge-arm64 and topiaforge-x64. Dart AOT '
          'executables cannot be combined safely with lipo.',
        );
      }
      await _copyMacCliPair(destinationRoot);
      return;
    }
    if (prebuiltCli.trim().isNotEmpty) {
      if (!File(prebuiltCli).existsSync()) {
        throw StateError('Prebuilt CLI was not found: $prebuiltCli');
      }
      File(prebuiltCli).copySync(destination);
      await fileOps.setExecutableBit(destination);
      return;
    }

    final cliApp = p.join(repositoryRoot, 'apps', 'topiaforge_cli');
    await _runDart([
      'pub',
      'get',
      '--enforce-lockfile',
    ], workingDirectory: cliApp);
    await _runDart([
      'compile',
      'exe',
      p.join('bin', 'topiaforge.dart'),
      '-o',
      destination,
    ], workingDirectory: cliApp);
    await fileOps.setExecutableBit(destination);
  }

  Future<void> _copyMacCliPair(String destinationRoot) async {
    final source = Directory(prebuiltCli);
    if (!source.existsSync()) {
      throw StateError(
        'The macOS prebuilt CLI must be a directory containing '
        'topiaforge-arm64 and topiaforge-x64. Dart AOT executables cannot be '
        'combined safely with lipo.',
      );
    }
    for (final name in const [macCliArm64FileName, macCliX64FileName]) {
      final input = File(p.join(source.path, name));
      if (!input.existsSync()) {
        throw StateError('The macOS prebuilt CLI is missing $name.');
      }
      final output = p.join(destinationRoot, name);
      input.copySync(output);
      await fileOps.setExecutableBit(output);
    }

    final dispatcher = File(p.join(destinationRoot, platform.cliFileName));
    dispatcher.writeAsStringSync(_macCliDispatcherScript, flush: true);
    await fileOps.setExecutableBit(dispatcher.path);
  }

  Future<void> _runDart(
    List<String> arguments, {
    required String workingDirectory,
  }) async {
    final command = await _resolveDartCommand();
    await processRunner.runChecked(
      command,
      arguments,
      workingDirectory: workingDirectory,
    );
  }

  Future<String> _resolveDartCommand() async {
    if (!isAotExecutable) {
      return Platform.resolvedExecutable;
    }
    final projectDart = _projectSdkCommand('dart');
    if (File(projectDart).existsSync()) {
      return projectDart;
    }
    if (await processRunner.commandExists('dart')) {
      return 'dart';
    }
    throw StateError(
      'Dart was not found at $projectDart or on PATH. '
      'Follow docs/ContributorSetup.md to select Flutter 3.44.6 with FVM.',
    );
  }

  Future<String> _resolveFlutterCommand() async {
    final projectFlutter = _projectSdkCommand('flutter');
    if (File(projectFlutter).existsSync()) {
      return projectFlutter;
    }
    if (await processRunner.commandExists('flutter')) {
      return 'flutter';
    }
    throw StateError(
      'Flutter was not found at $projectFlutter or on PATH. '
      'Follow docs/ContributorSetup.md to select Flutter 3.44.6 with FVM.',
    );
  }

  String _projectSdkCommand(String tool) => p.join(
    repositoryRoot,
    '.fvm',
    'flutter_sdk',
    'bin',
    Platform.isWindows ? '$tool.bat' : tool,
  );

  Future<void> _writeMacCliEntrypoint(String stageRoot) async {
    final entrypoint = File(p.join(stageRoot, 'topiaforge'));
    entrypoint.writeAsStringSync('''
#!/bin/sh
set -eu
DIR="\$(CDPATH= cd -- "\$(dirname -- "\$0")" && pwd)"
exec "\$DIR/TopiaForge.app/Contents/Resources/TopiaForge/topiaforge" "\$@"
''');
    await fileOps.setExecutableBit(entrypoint.path);
  }

  String? _locateMacApp(String stageRoot) => _findAppBundle(stageRoot);

  String? _findAppBundle(String root) {
    final asFile = Directory(root);
    if (p.basename(root) == _macAppBundleName && asFile.existsSync()) {
      return root;
    }
    final preferred = Directory(p.join(root, _macAppBundleName));
    if (preferred.existsSync()) {
      return preferred.path;
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
    distSourceRoot: prebuiltDist,
    dotnetSdkResolver: dotnetSdkResolver,
  );
}

const _macAppBundleName = 'TopiaForge.app';
const _macCliDispatcherScript = '''#!/bin/sh
set -eu
DIR="\$(CDPATH= cd -- "\$(dirname -- "\$0")" && pwd)"
case "\$(uname -m)" in
  arm64) exec "\$DIR/topiaforge-arm64" "\$@" ;;
  x86_64) exec "\$DIR/topiaforge-x64" "\$@" ;;
  *) echo "Unsupported macOS architecture: \$(uname -m)" >&2; exit 64 ;;
esac
''';
const _isAotExecutable = bool.fromEnvironment('dart.vm.product');
