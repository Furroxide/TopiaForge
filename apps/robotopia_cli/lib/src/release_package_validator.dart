import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';
import 'release_package_models.dart';

class ReleasePackageValidator {
  ReleasePackageValidator({
    required this.platform,
    required this.zipPath,
    this.requireMacUniversal = false,
    this.requireRuntimePayload = true,
    this.requireLauncher = true,
    this.requireDistPackages = true,
    this.runCliSmoke = true,
    this.processRunner = const ReleaseProcessRunner(),
  }) : fileOps = ReleaseFileOps(processRunner: processRunner);

  final ReleasePackagePlatform platform;
  final String zipPath;
  final bool requireMacUniversal;
  final bool requireRuntimePayload;
  final bool requireLauncher;
  final bool requireDistPackages;
  final bool runCliSmoke;
  final ReleaseProcessRunner processRunner;
  final ReleaseFileOps fileOps;

  Future<void> validate() async {
    final zip = File(zipPath).absolute;
    if (!zip.existsSync()) {
      throw StateError('Release package was not found: ${zip.path}');
    }
    final tempRoot = Directory.systemTemp.createTempSync(
      'quantumworks-package-test-',
    );
    try {
      await fileOps.extractPlatformZip(zip, tempRoot, platform);
      if (platform == ReleasePackagePlatform.macos) {
        await _validateMacPackage(tempRoot.path);
      } else {
        await _validateFlatPackage(tempRoot.path);
      }
      stdout.writeln('Package smoke test passed: ${zip.path}');
    } finally {
      if (tempRoot.existsSync()) {
        tempRoot.deleteSync(recursive: true);
      }
    }
  }

  Future<void> _validateMacPackage(String root) async {
    final app = p.join(root, 'QuantumWorks.app');
    _assertPath(app, 'macOS package must include QuantumWorks.app.');
    final payload = p.join(app, 'Contents', 'Resources', 'QuantumWorks');
    final cli = p.join(payload, 'robotopia');
    final shim = p.join(root, 'robotopia');
    final appBinary = p.join(app, 'Contents', 'MacOS', 'QuantumWorks');

    _assertPayload(payload);
    if (requireRuntimePayload) {
      _assertRuntimePayload(payload);
    }
    await _assertCliRuns(cli);
    await _assertCliRuns(shim);
    await _assertMacUniversal(cli, 'robotopia CLI');
    await _assertMacUniversal(appBinary, 'QuantumWorks.app binary');
  }

  Future<void> _validateFlatPackage(String root) async {
    final cli = platform == ReleasePackagePlatform.windows
        ? p.join(root, 'robotopia.exe')
        : p.join(root, 'robotopia');
    if (requireLauncher) {
      _assertPath(p.join(root, 'launcher'), 'Package must include launcher/.');
    }
    _assertPayload(root);
    if (requireRuntimePayload) {
      _assertRuntimePayload(root);
    }
    await _assertCliRuns(cli);
    if (requireLauncher && platform == ReleasePackagePlatform.linux) {
      await _assertExecutable(
        p.join(root, 'launcher', 'robotopia_launcher_flutter'),
      );
    }
    if (requireLauncher && platform == ReleasePackagePlatform.windows) {
      _assertPath(
        p.join(root, 'launcher', 'robotopia_launcher_flutter.exe'),
        'Windows package must include launcher exe.',
      );
    }
  }

  void _assertPayload(String payloadRoot) {
    _assertPath(p.join(payloadRoot, 'tools'), 'Package must include tools/.');
    _assertPath(
      p.join(payloadRoot, 'templates'),
      'Package must include templates/.',
    );
    _assertPath(p.join(payloadRoot, 'docs'), 'Package must include docs/.');
    _assertPath(
      p.join(payloadRoot, 'bindings'),
      'Package must include bindings/.',
    );
    _assertPath(
      p.join(payloadRoot, 'baselines'),
      'Package must include baselines/.',
    );
    _assertPath(
      p.join(payloadRoot, 'THIRD_PARTY_NOTICES.md'),
      'Package must include third-party notices.',
    );
    _assertPath(
      p.join(payloadRoot, 'dist', 'vpm', 'index.json'),
      'Package must include dist/vpm/index.json.',
    );
    if (requireRuntimePayload) {
      _assertPath(
        p.join(payloadRoot, platform.gameCompatExtractorFileName),
        'Package must include the GameCompat extractor.',
      );
    }
    if (requireDistPackages) {
      final packages = Directory(p.join(payloadRoot, 'dist'))
          .listSync()
          .whereType<File>()
          .where((file) => p.extension(file.path) == '.robotopiamod');
      if (packages.isEmpty) {
        throw StateError(
          'Package must include at least one dist/*.robotopiamod file.',
        );
      }
    }
  }

  void _assertRuntimePayload(String payloadRoot) {
    final bundle = p.join(
      payloadRoot,
      'third_party',
      'BepInEx',
      platform.bepInExBundleName,
    );
    if (platform == ReleasePackagePlatform.macos) {
      _assertPath(
        p.join(bundle, 'run_bepinex.sh'),
        'macOS package must include the BepInEx run script.',
      );
      _assertPath(
        p.join(bundle, 'libdoorstop.dylib'),
        'macOS package must include libdoorstop.',
      );
    } else {
      _assertPath(
        p.join(bundle, 'winhttp.dll'),
        'Package must include Doorstop.',
      );
      _assertPath(
        p.join(bundle, 'doorstop_config.ini'),
        'Package must include Doorstop config.',
      );
    }
    _assertPath(
      p.join(bundle, 'BepInEx', 'core'),
      'Package must include BepInEx core.',
    );

    final loaderDir = p.join(
      payloadRoot,
      'src',
      'Robotopia.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    );
    _assertPath(
      p.join(loaderDir, 'Robotopia.ModManager.dll'),
      'Package must include the loader.',
    );
    _assertPath(
      p.join(loaderDir, 'Robotopia.Mods.UnityUi.dll'),
      'Package must include the UI kit.',
    );

    if (platform == ReleasePackagePlatform.windows) {
      _assertPath(
        p.join(payloadRoot, 'winhttp.dll'),
        'Windows package must include the game-overlay Doorstop.',
      );
      _assertPath(
        p.join(
          payloadRoot,
          'BepInEx',
          'plugins',
          'RobotopiaModManager',
          'Robotopia.ModManager.dll',
        ),
        'Windows package must include the overlay loader.',
      );
    }
  }

  Future<void> _assertCliRuns(String cliPath) async {
    await _assertExecutable(cliPath);
    if (!runCliSmoke) {
      return;
    }
    final result = await Process.run(cliPath, ['--help']);
    if (result.exitCode != 0) {
      throw StateError('CLI help failed with exit ${result.exitCode}.');
    }
    final output = '${result.stdout}\n${result.stderr}';
    if (!output.contains('QuantumWorks CLI')) {
      throw StateError('CLI help output did not contain the expected banner.');
    }
  }

  Future<void> _assertExecutable(String path) async {
    _assertPath(path, 'Expected executable file.');
    if (platform == ReleasePackagePlatform.windows) {
      return;
    }
    final result = await Process.run('test', ['-x', path]);
    if (result.exitCode != 0) {
      throw StateError('Expected executable bit to be set: $path');
    }
  }

  Future<void> _assertMacUniversal(String path, String label) async {
    if (!requireMacUniversal) {
      return;
    }
    final result = await Process.run('lipo', ['-archs', path]);
    if (result.exitCode != 0) {
      throw StateError('lipo failed for $label.');
    }
    final archs = result.stdout.toString();
    if (!archs.contains('arm64') || !archs.contains('x86_64')) {
      throw StateError('$label is not universal. Found archs: $archs');
    }
  }

  void _assertPath(String path, String message) {
    if (!FileSystemEntity.typeSync(path).exists) {
      throw StateError('$message Missing path: $path');
    }
  }
}

extension on FileSystemEntityType {
  bool get exists => this != FileSystemEntityType.notFound;
}
