import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';

class WindowsPackageSigner {
  WindowsPackageSigner({
    this.processRunner = const ReleaseProcessRunner(),
    this.requireTrustedSignature = false,
    bool? isWindows,
    Map<String, String>? environment,
  }) : isWindows = isWindows ?? Platform.isWindows,
       environment = environment ?? Platform.environment;

  final ReleaseProcessRunner processRunner;
  final bool requireTrustedSignature;
  final bool isWindows;
  final Map<String, String> environment;

  Future<void> signIfConfigured(String stageRoot) async {
    if (!isWindows) {
      if (requireTrustedSignature) {
        throw StateError('Windows release signing must run on Windows.');
      }
      return;
    }

    final encodedCertificate = environment['WINDOWS_CERTIFICATE_PFX'] ?? '';
    final password = environment['WINDOWS_CERTIFICATE_PASSWORD'] ?? '';
    if (encodedCertificate.trim().isEmpty || password.isEmpty) {
      if (requireTrustedSignature) {
        throw StateError(
          'A complete Authenticode signing configuration is required for a '
          'public Windows release.',
        );
      }
      stderr.writeln(
        releaseWarning(
          'Windows signing secrets are incomplete; package executables remain unsigned.',
        ),
      );
      return;
    }

    final signTool = await _resolveSignTool();
    if (signTool == null) {
      throw StateError('SignTool is required for a public Windows release.');
    }
    final timestamp = _timestampUrl();
    final targets = _releaseTargets(stageRoot);
    if (targets.length != 3) {
      throw StateError(
        'Windows signing requires the CLI, GameCompat extractor, and launcher executable.',
      );
    }

    final signingTemp = Directory.systemTemp.createTempSync(
      'topiaforge-windows-signing-',
    );
    final certificate = File(p.join(signingTemp.path, 'certificate.pfx'));
    try {
      certificate.createSync(exclusive: true);
      certificate.writeAsBytesSync(
        base64Decode(encodedCertificate.replaceAll(RegExp(r'\s+'), '')),
        flush: true,
      );
      for (final target in targets) {
        await processRunner.runChecked(
          signTool,
          [
            'sign',
            '/fd',
            'SHA256',
            '/tr',
            timestamp,
            '/td',
            'SHA256',
            '/f',
            certificate.path,
            '/p',
            password,
            target,
          ],
          redactedValueOptions: const {'/p'},
        );
        await processRunner.runChecked(signTool, [
          'verify',
          '/pa',
          '/all',
          '/tw',
          '/v',
          target,
        ]);
      }
    } on FormatException catch (error) {
      throw StateError('WINDOWS_CERTIFICATE_PFX is not valid base64: $error');
    } finally {
      if (signingTemp.existsSync()) {
        signingTemp.deleteSync(recursive: true);
      }
    }
  }

  Future<void> verifyTrustedSignatures(String stageRoot) async {
    if (!isWindows) {
      throw StateError('Windows signature verification must run on Windows.');
    }
    final signTool = await _resolveSignTool();
    if (signTool == null) {
      throw StateError(
        'SignTool is required to verify Windows release signatures.',
      );
    }
    final targets = _releaseTargets(stageRoot);
    if (targets.length != 3) {
      throw StateError(
        'Windows signature verification requires the CLI, GameCompat '
        'extractor, and launcher executable.',
      );
    }
    for (final target in targets) {
      await processRunner.runChecked(signTool, [
        'verify',
        '/pa',
        '/all',
        '/tw',
        '/v',
        target,
      ]);
    }
  }

  List<String> _releaseTargets(String stageRoot) => [
    p.join(stageRoot, 'topiaforge.exe'),
    p.join(stageRoot, 'TopiaForge.GameCompat.Extractor.exe'),
    p.join(stageRoot, 'launcher', 'topiaforge_launcher.exe'),
  ].where(FileSystemEntity.isFileSync).toList();

  String _timestampUrl() {
    final value =
        environment['WINDOWS_TIMESTAMP_URL']?.trim().isNotEmpty == true
        ? environment['WINDOWS_TIMESTAMP_URL']!.trim()
        : 'https://timestamp.digicert.com';
    final uri = Uri.tryParse(value);
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host.isEmpty ||
        uri.userInfo.isNotEmpty ||
        uri.fragment.isNotEmpty) {
      throw StateError(
        'WINDOWS_TIMESTAMP_URL must be a credential-free HTTPS URL.',
      );
    }
    return uri.toString();
  }

  Future<String?> _resolveSignTool() async {
    if (await processRunner.commandExists('signtool')) {
      return 'signtool';
    }
    final roots = <String>{
      if ((environment['ProgramFiles(x86)'] ?? '').isNotEmpty)
        environment['ProgramFiles(x86)']!,
      if ((environment['ProgramFiles'] ?? '').isNotEmpty)
        environment['ProgramFiles']!,
    };
    final candidates = <String>[];
    for (final root in roots) {
      final bin = Directory(p.join(root, 'Windows Kits', '10', 'bin'));
      if (!bin.existsSync()) {
        continue;
      }
      for (final version
          in bin.listSync(followLinks: false).whereType<Directory>()) {
        final candidate = p.join(version.path, 'x64', 'signtool.exe');
        if (File(candidate).existsSync()) {
          candidates.add(candidate);
        }
      }
    }
    candidates.sort((left, right) => right.compareTo(left));
    return candidates.firstOrNull;
  }
}

extension<T> on List<T> {
  T? get firstOrNull => isEmpty ? null : first;
}
