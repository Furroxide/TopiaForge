import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';
import 'release_package_models.dart';

class MacPackageSigner {
  MacPackageSigner({
    this.processRunner = const ReleaseProcessRunner(),
    this.requireTrustedSignature = false,
    bool? isMacOS,
    Map<String, String>? environment,
  }) : _isMacOS = isMacOS ?? Platform.isMacOS,
       _environment = Map.unmodifiable(environment ?? Platform.environment);

  final ReleaseProcessRunner processRunner;
  final bool requireTrustedSignature;
  final bool _isMacOS;
  final Map<String, String> _environment;

  Future<void> signIfConfigured(String appBundle, String stageRoot) async {
    if (!_isMacOS) {
      return;
    }
    if (!await processRunner.commandExists('codesign')) {
      throw StateError(
        'codesign is required to produce a runnable macOS package.',
      );
    }

    final hasSigningSecrets =
        _hasEnv('MACOS_CERTIFICATE_P12') &&
        _hasEnv('MACOS_CERTIFICATE_PASSWORD') &&
        _hasEnv('MACOS_DEVELOPER_ID_APPLICATION');
    if (!hasSigningSecrets) {
      if (requireTrustedSignature) {
        throw StateError(
          'A complete Developer ID signing configuration is required for a '
          'public macOS release.',
        );
      }
      stderr.writeln(
        releaseWarning(
          'macOS signing secrets are incomplete; applying an ad-hoc app signature when possible.',
        ),
      );
      try {
        // Hardened runtime library validation cannot establish a common Team
        // ID between separately ad-hoc-signed code objects. Enabling it here
        // makes the otherwise valid dry-run app abort in dyld while loading
        // embedded Flutter plugin frameworks. Public packages take the
        // Developer ID path below and always retain hardened runtime.
        await _codeSign(appBundle, '-', '', deep: true, hardenedRuntime: false);
        await processRunner.runChecked('codesign', [
          '--verify',
          '--deep',
          '--strict',
          '--verbose=2',
          appBundle,
        ]);
      } on Object catch (error) {
        throw StateError(
          'Ad-hoc signing failed; refusing to emit an unusable macOS package. '
          '$error',
        );
      }
      return;
    }

    final signingTemp = Directory.systemTemp.createTempSync(
      'topiaforge-signing-',
    );
    final keychain = p.join(signingTemp.path, 'signing.keychain-db');
    final keychainPassword = _randomSecret();
    final certPath = p.join(signingTemp.path, 'certificate.p12');

    try {
      final encoded = _environment['MACOS_CERTIFICATE_P12']!.replaceAll(
        RegExp(r'\s+'),
        '',
      );
      File(certPath).createSync(exclusive: true);
      await processRunner.runChecked('/bin/chmod', ['600', certPath]);
      File(certPath).writeAsBytesSync(base64Decode(encoded));

      await processRunner.runChecked(
        'security',
        ['create-keychain', '-p', keychainPassword, keychain],
        redactedValueOptions: const {'-p'},
      );
      await processRunner.runChecked('security', [
        'set-keychain-settings',
        '-lut',
        '21600',
        keychain,
      ]);
      await processRunner.runChecked(
        'security',
        ['unlock-keychain', '-p', keychainPassword, keychain],
        redactedValueOptions: const {'-p'},
      );
      await processRunner.runChecked(
        'security',
        [
          'import',
          certPath,
          '-P',
          _environment['MACOS_CERTIFICATE_PASSWORD']!,
          '-A',
          '-t',
          'cert',
          '-f',
          'pkcs12',
          '-k',
          keychain,
        ],
        redactedValueOptions: const {'-P'},
      );
      await processRunner.runChecked(
        'security',
        [
          'set-key-partition-list',
          '-S',
          'apple-tool:,apple:',
          '-s',
          '-k',
          keychainPassword,
          keychain,
        ],
        redactedValueOptions: const {'-k'},
      );

      final identity = _environment['MACOS_DEVELOPER_ID_APPLICATION']!;
      final payloadRoot = p.join(
        appBundle,
        'Contents',
        'Resources',
        'TopiaForge',
      );
      for (final binary in [
        p.join(payloadRoot, macCliArm64FileName),
        p.join(payloadRoot, macCliX64FileName),
        p.join(payloadRoot, 'TopiaForge.GameCompat.Extractor'),
      ]) {
        if (File(binary).existsSync()) {
          await _codeSign(binary, identity, keychain);
        }
      }
      await _codeSign(appBundle, identity, keychain, deep: true);
      await processRunner.runChecked('codesign', [
        '--verify',
        '--deep',
        '--strict',
        '--verbose=2',
        appBundle,
      ]);
      await _notarizeIfConfigured(appBundle, stageRoot);
    } finally {
      if (File(certPath).existsSync()) {
        File(certPath).deleteSync();
      }
      if (File(keychain).existsSync()) {
        try {
          await processRunner.runChecked('security', [
            'delete-keychain',
            keychain,
          ]);
        } on Object catch (error) {
          stderr.writeln(
            releaseWarning(
              'Could not remove temporary signing keychain: $error',
            ),
          );
        }
      }
      if (signingTemp.existsSync()) {
        signingTemp.deleteSync(recursive: true);
      }
    }
  }

  Future<void> _codeSign(
    String path,
    String identity,
    String keychain, {
    bool deep = false,
    bool hardenedRuntime = true,
  }) async {
    final args = ['--force'];
    if (hardenedRuntime) {
      args.addAll(['--options', 'runtime']);
    }
    if (identity != '-') {
      args.add('--timestamp');
    }
    if (deep) {
      args.add('--deep');
    }
    args.addAll(['--sign', identity]);
    if (keychain.isNotEmpty) {
      args.addAll(['--keychain', keychain]);
    }
    args.add(path);
    await processRunner.runChecked('codesign', args);
  }

  Future<void> _notarizeIfConfigured(String appBundle, String stageRoot) async {
    final hasNotarySecrets =
        _hasEnv('MACOS_NOTARY_APPLE_ID') &&
        _hasEnv('MACOS_NOTARY_PASSWORD') &&
        _hasEnv('MACOS_NOTARY_TEAM_ID');
    if (!hasNotarySecrets) {
      if (requireTrustedSignature) {
        throw StateError(
          'A complete Apple notarization configuration is required for a '
          'public macOS release.',
        );
      }
      stderr.writeln(
        releaseWarning(
          'macOS notary secrets are incomplete; package will be signed but not notarized.',
        ),
      );
      return;
    }

    final notaryTemp = Directory.systemTemp.createTempSync(
      'topiaforge-notary-',
    );
    final notaryZip = p.join(notaryTemp.path, 'submission.zip');
    try {
      await processRunner.runChecked('/usr/bin/ditto', [
        '-c',
        '-k',
        '--keepParent',
        p.basename(appBundle),
        notaryZip,
      ], workingDirectory: stageRoot);
      await processRunner.runChecked(
        'xcrun',
        [
          'notarytool',
          'submit',
          notaryZip,
          '--apple-id',
          _environment['MACOS_NOTARY_APPLE_ID']!,
          '--password',
          _environment['MACOS_NOTARY_PASSWORD']!,
          '--team-id',
          _environment['MACOS_NOTARY_TEAM_ID']!,
          '--wait',
        ],
        redactedValueOptions: const {'--apple-id', '--password'},
      );
      await processRunner.runChecked('xcrun', ['stapler', 'staple', appBundle]);
      await processRunner.runChecked('xcrun', [
        'stapler',
        'validate',
        appBundle,
      ]);
      await processRunner.runChecked('spctl', [
        '--assess',
        '--type',
        'execute',
        '--verbose=2',
        appBundle,
      ]);
    } finally {
      if (notaryTemp.existsSync()) {
        notaryTemp.deleteSync(recursive: true);
      }
    }
  }

  bool _hasEnv(String name) {
    final value = _environment[name];
    return value != null && value.trim().isNotEmpty;
  }

  String _randomSecret() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64UrlEncode(bytes);
  }
}
