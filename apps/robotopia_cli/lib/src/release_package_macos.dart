import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';

class MacPackageSigner {
  MacPackageSigner({this.processRunner = const ReleaseProcessRunner()});

  final ReleaseProcessRunner processRunner;

  Future<void> signIfConfigured(String appBundle, String stageRoot) async {
    if (!Platform.isMacOS) {
      return;
    }
    if (!await processRunner.commandExists('codesign')) {
      stderr.writeln(
        releaseWarning(
          'codesign was not found; macOS package will be unsigned.',
        ),
      );
      return;
    }

    final hasSigningSecrets =
        _hasEnv('MACOS_CERTIFICATE_P12') &&
        _hasEnv('MACOS_CERTIFICATE_PASSWORD') &&
        _hasEnv('MACOS_DEVELOPER_ID_APPLICATION');
    if (!hasSigningSecrets) {
      stderr.writeln(
        releaseWarning(
          'macOS signing secrets are incomplete; applying an ad-hoc app signature when possible.',
        ),
      );
      try {
        await _codeSign(appBundle, '-', '', deep: true);
        await processRunner.runChecked('codesign', [
          '--verify',
          '--deep',
          '--strict',
          '--verbose=2',
          appBundle,
        ]);
      } on Object catch (error) {
        stderr.writeln(
          releaseWarning(
            'Ad-hoc signing failed; continuing with an unsigned macOS package. $error',
          ),
        );
      }
      return;
    }

    final temp = Directory.systemTemp.path;
    final keychain = p.join(
      temp,
      'quantumworks-signing-${DateTime.now().microsecondsSinceEpoch}.keychain-db',
    );
    final keychainPassword = DateTime.now().microsecondsSinceEpoch.toString();
    final certPath = p.join(
      temp,
      'quantumworks-cert-${DateTime.now().microsecondsSinceEpoch}.p12',
    );

    try {
      final encoded = Platform.environment['MACOS_CERTIFICATE_P12']!.replaceAll(
        RegExp(r'\s+'),
        '',
      );
      File(certPath).writeAsBytesSync(base64Decode(encoded));

      await processRunner.runChecked('security', [
        'create-keychain',
        '-p',
        keychainPassword,
        keychain,
      ]);
      await processRunner.runChecked('security', [
        'set-keychain-settings',
        '-lut',
        '21600',
        keychain,
      ]);
      await processRunner.runChecked('security', [
        'unlock-keychain',
        '-p',
        keychainPassword,
        keychain,
      ]);
      await processRunner.runChecked('security', [
        'import',
        certPath,
        '-P',
        Platform.environment['MACOS_CERTIFICATE_PASSWORD']!,
        '-A',
        '-t',
        'cert',
        '-f',
        'pkcs12',
        '-k',
        keychain,
      ]);
      await processRunner.runChecked('security', [
        'set-key-partition-list',
        '-S',
        'apple-tool:,apple:',
        '-s',
        '-k',
        keychainPassword,
        keychain,
      ]);

      final identity = Platform.environment['MACOS_DEVELOPER_ID_APPLICATION']!;
      final payloadRoot = p.join(
        appBundle,
        'Contents',
        'Resources',
        'QuantumWorks',
      );
      for (final binary in [
        p.join(payloadRoot, 'robotopia'),
        p.join(payloadRoot, 'Robotopia.GameCompat.Extractor'),
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
        await Process.run('security', ['delete-keychain', keychain]);
      }
    }
  }

  Future<void> _codeSign(
    String path,
    String identity,
    String keychain, {
    bool deep = false,
  }) async {
    final args = ['--force'];
    if (identity != '-') {
      args.addAll(['--options', 'runtime', '--timestamp']);
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
      stderr.writeln(
        releaseWarning(
          'macOS notary secrets are incomplete; package will be signed but not notarized.',
        ),
      );
      return;
    }

    final notaryZip = p.join(
      Directory.systemTemp.path,
      'quantumworks-notary-${DateTime.now().microsecondsSinceEpoch}.zip',
    );
    try {
      await processRunner.runChecked('/usr/bin/ditto', [
        '-c',
        '-k',
        '--keepParent',
        p.basename(appBundle),
        notaryZip,
      ], workingDirectory: stageRoot);
      await processRunner.runChecked('xcrun', [
        'notarytool',
        'submit',
        notaryZip,
        '--apple-id',
        Platform.environment['MACOS_NOTARY_APPLE_ID']!,
        '--password',
        Platform.environment['MACOS_NOTARY_PASSWORD']!,
        '--team-id',
        Platform.environment['MACOS_NOTARY_TEAM_ID']!,
        '--wait',
      ]);
      await processRunner.runChecked('xcrun', ['stapler', 'staple', appBundle]);
    } finally {
      if (File(notaryZip).existsSync()) {
        File(notaryZip).deleteSync();
      }
    }
  }

  bool _hasEnv(String name) {
    final value = Platform.environment[name];
    return value != null && value.trim().isNotEmpty;
  }
}
