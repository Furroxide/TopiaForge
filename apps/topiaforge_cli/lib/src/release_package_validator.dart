import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_loader_payload.dart';
import 'release_package_io.dart';
import 'release_package_models.dart';
import 'release_package_notices.dart';
import 'release_package_windows.dart';
import 'release_sdk_payload.dart';

part 'release_package_validator_helpers.dart';
part 'release_package_validator_smoke.dart';

class ReleasePackageValidator {
  ReleasePackageValidator({
    required this.platform,
    required this.zipPath,
    this.requireMacUniversal = false,
    this.requireWindowsSignature = false,
    this.requireMacTrust = false,
    this.expectedMacTeamId = '',
    this.requireRuntimePayload = true,
    this.requireLauncher = true,
    this.requireDistPackages = true,
    this.runCliSmoke = false,
    this.processRunner = const ReleaseProcessRunner(),
  }) : fileOps = ReleaseFileOps(processRunner: processRunner);

  final ReleasePackagePlatform platform;
  final String zipPath;
  final bool requireMacUniversal;
  final bool requireWindowsSignature;
  final bool requireMacTrust;
  final String expectedMacTeamId;
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
      'topiaforge-package-test-',
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
    final app = p.join(root, 'TopiaForge.app');
    _assertPath(app, 'macOS package must include TopiaForge.app.');
    final payload = p.join(app, 'Contents', 'Resources', 'TopiaForge');
    final cli = p.join(payload, 'topiaforge');
    final entrypoint = p.join(root, 'topiaforge');
    final appBinary = p.join(app, 'Contents', 'MacOS', 'topiaforge_launcher');

    _assertPayload(payload);
    if (requireRuntimePayload) {
      _assertRuntimePayload(payload);
      await _assertExecutable(
        p.join(payload, platform.gameCompatExtractorFileName),
      );
    }
    if (requireLauncher) {
      _assertFlutterNotices(app);
    }
    await _assertCliRuns(cli);
    await _assertCliRuns(entrypoint);
    await _assertMacCliPair(payload);
    await _assertMacUniversal(appBinary, 'TopiaForge.app binary');
    if (requireRuntimePayload) {
      await _assertMacUniversal(
        p.join(payload, platform.gameCompatExtractorFileName),
        'GameCompat extractor',
      );
    }
    await _assertBundledMacBinariesUniversal(app);
    await _assertMacTrust(app);
  }

  Future<void> _validateFlatPackage(String root) async {
    final cli = platform == ReleasePackagePlatform.windows
        ? p.join(root, 'topiaforge.exe')
        : p.join(root, 'topiaforge');
    if (requireLauncher) {
      _assertPath(p.join(root, 'launcher'), 'Package must include launcher/.');
      _assertFlutterNotices(p.join(root, 'launcher'));
    }
    _assertPayload(root);
    if (requireRuntimePayload) {
      _assertRuntimePayload(root);
      await _assertExecutable(
        p.join(root, platform.gameCompatExtractorFileName),
      );
    }
    await _assertCliRuns(cli);
    if (requireLauncher && platform == ReleasePackagePlatform.linux) {
      await _assertExecutable(p.join(root, 'launcher', 'topiaforge_launcher'));
    }
    if (requireLauncher && platform == ReleasePackagePlatform.windows) {
      _assertPath(
        p.join(root, 'launcher', 'topiaforge_launcher.exe'),
        'Windows package must include launcher exe.',
      );
    }
    if (platform == ReleasePackagePlatform.windows && requireWindowsSignature) {
      await WindowsPackageSigner(
        processRunner: processRunner,
        requireTrustedSignature: true,
      ).verifyTrustedSignatures(root);
    }
  }

  void _assertPayload(String payloadRoot) {
    const ReleaseSdkPayloadValidator().validate(payloadRoot);
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
    for (final license in dartCliLicenseNames) {
      _assertPath(
        p.join(payloadRoot, 'third_party', 'dart', 'LICENSES', license),
        'Package must include the standalone Dart CLI license bundle.',
      );
    }
    for (final license in const [
      'BepInEx-MIT.txt',
      'UnityDoorstop-LGPL-2.1.txt',
      'HarmonyX-MIT.txt',
      'Harmony-MIT.txt',
      'MonoMod-MIT.txt',
      'Mono.Cecil-MIT.txt',
    ]) {
      _assertPath(
        p.join(payloadRoot, 'third_party', 'BepInEx', 'LICENSES', license),
        'Package must include the bundled BepInEx dependency licenses.',
      );
    }
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
          .where((file) => p.extension(file.path) == '.topiaforgemod');
      if (packages.isEmpty) {
        throw StateError(
          'Package must include at least one dist/*.topiaforgemod file.',
        );
      }
    }
  }

  void _assertRuntimePayload(String payloadRoot) {
    for (final notice in const [
      'LICENSE.txt',
      'ThirdPartyNotices.txt',
      'MetadataLoadContext-LICENSE.txt',
      'MetadataLoadContext-ThirdPartyNotices.txt',
      'VERSION.txt',
    ]) {
      _assertPath(
        p.join(payloadRoot, 'third_party', 'dotnet', notice),
        'Package must include the self-contained .NET runtime notices.',
      );
    }
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
      'TopiaForge.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    );
    for (final dependency in releaseLoaderDlls) {
      _assertPath(
        p.join(loaderDir, dependency),
        'Package must include managed loader file $dependency.',
      );
    }
    _assertRuntimeLoaderProvenance(payloadRoot);

    if (platform == ReleasePackagePlatform.windows) {
      _assertPath(
        p.join(payloadRoot, 'winhttp.dll'),
        'Windows package must include the game-overlay Doorstop.',
      );
      final overlay = p.join(
        payloadRoot,
        'BepInEx',
        'plugins',
        'TopiaForge.ModManager',
      );
      for (final dependency in releaseLoaderDlls) {
        _assertPath(
          p.join(overlay, dependency),
          'Windows overlay must include managed loader file $dependency.',
        );
      }
      validateWindowsLoaderOverlay(payloadRoot);
    }
  }

  Future<void> _assertExecutable(String path) async {
    _assertPath(path, 'Expected executable file.');
    if (platform == ReleasePackagePlatform.windows || Platform.isWindows) {
      // Windows does not expose an extracted ZIP entry's POSIX executable
      // bits. Native Unix validation still checks the mode with `test -x`.
      return;
    }
    final result = await processRunner.runResult('test', ['-x', path]);
    if (result.exitCode != 0) {
      throw StateError('Expected executable bit to be set: $path');
    }
  }

  Future<void> _assertMacUniversal(String path, String label) async {
    if (!requireMacUniversal) {
      return;
    }
    final result = await processRunner.runResult('lipo', ['-archs', path]);
    if (result.exitCode != 0) {
      throw StateError('lipo failed for $label.');
    }
    final archs = result.stdout.toString();
    if (!archs.contains('arm64') || !archs.contains('x86_64')) {
      throw StateError('$label is not universal. Found archs: $archs');
    }
  }

  Future<void> _assertMacCliPair(String payloadRoot) async {
    if (!requireMacUniversal) {
      return;
    }
    final arm64 = p.join(payloadRoot, macCliArm64FileName);
    final x64 = p.join(payloadRoot, macCliX64FileName);
    await _assertExecutable(arm64);
    await _assertExecutable(x64);
    await _assertMacArchitecture(arm64, 'arm64');
    await _assertMacArchitecture(x64, 'x86_64');
  }

  Future<void> _assertMacArchitecture(String path, String expected) async {
    final result = await processRunner.runResult('lipo', ['-archs', path]);
    if (result.exitCode != 0) {
      throw StateError('lipo failed for ${p.basename(path)}.');
    }
    final architectures = result.stdout
        .toString()
        .trim()
        .split(RegExp(r'\s+'))
        .where((value) => value.isNotEmpty)
        .toSet();
    if (architectures.length != 1 || !architectures.contains(expected)) {
      throw StateError(
        '${p.basename(path)} must contain only $expected. '
        'Found: ${architectures.join(' ')}',
      );
    }
  }

  Future<void> _assertBundledMacBinariesUniversal(String appPath) async {
    if (!requireMacUniversal) {
      return;
    }
    final app = Directory(appPath);
    for (final entity in app.listSync(recursive: true, followLinks: false)) {
      if (entity is! File || !_hasMachOMagic(entity)) {
        continue;
      }
      final label = p.relative(entity.path, from: appPath);
      if (label.endsWith('/$macCliArm64FileName') ||
          label.endsWith('/$macCliX64FileName')) {
        continue;
      }
      await _assertMacUniversal(entity.path, label);
    }
  }

  bool _hasMachOMagic(File file) {
    RandomAccessFile? handle;
    try {
      handle = file.openSync();
      final bytes = handle.readSync(4);
      if (bytes.length != 4) {
        return false;
      }
      final magic = bytes.fold<int>(0, (value, byte) => (value << 8) | byte);
      return const {
        0xfeedface,
        0xcefaedfe,
        0xfeedfacf,
        0xcffaedfe,
        0xcafebabe,
        0xbebafeca,
        0xcafebabf,
        0xbfbafeca,
      }.contains(magic);
    } on FileSystemException {
      return false;
    } finally {
      handle?.closeSync();
    }
  }

  Future<void> _assertMacTrust(String appPath) async {
    if (!requireMacTrust) return;
    if (!Platform.isMacOS) {
      throw StateError('Final macOS trust validation must run on macOS.');
    }
    final teamId = expectedMacTeamId.trim().isNotEmpty
        ? expectedMacTeamId.trim()
        : (Platform.environment['MACOS_NOTARY_TEAM_ID'] ?? '').trim();
    if (teamId.isEmpty) {
      throw StateError('Expected macOS Developer Team ID is required.');
    }
    await _requireSuccess('codesign', [
      '--verify',
      '--deep',
      '--strict',
      '--verbose=4',
      appPath,
    ], label: 'app signature');
    for (final entity in Directory(
      appPath,
    ).listSync(recursive: true, followLinks: false).whereType<File>()) {
      if (!_hasMachOMagic(entity)) continue;
      await _requireSuccess('codesign', [
        '--verify',
        '--strict',
        '--verbose=4',
        entity.path,
      ], label: p.relative(entity.path, from: appPath));
      final details = await processRunner.runResult('codesign', [
        '-d',
        '--verbose=4',
        entity.path,
      ]);
      final output = '${details.stdout}\n${details.stderr}';
      if (details.exitCode != 0 ||
          output.contains('Signature=adhoc') ||
          !output.contains('Authority=Developer ID Application:') ||
          !output.contains('TeamIdentifier=$teamId')) {
        throw StateError(
          'macOS code-signing identity or Team ID is invalid for '
          '${p.relative(entity.path, from: appPath)}.',
        );
      }
    }
    await _requireSuccess('xcrun', [
      'stapler',
      'validate',
      appPath,
    ], label: 'notarization ticket');
    await _requireSuccess('xattr', [
      '-w',
      'com.apple.quarantine',
      '0081;00000000;TopiaForge release validation;',
      appPath,
    ], label: 'quarantine simulation');
    await _requireSuccess('spctl', [
      '--assess',
      '--type',
      'execute',
      '--verbose=4',
      appPath,
    ], label: 'Gatekeeper assessment');
  }
}
