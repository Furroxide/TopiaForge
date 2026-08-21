import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_package_io.dart';

class WindowsPackageSigner {
  WindowsPackageSigner({
    this.processRunner = const ReleaseProcessRunner(),
    this.requireTrustedSignature = false,
    this.expectedSignerCertificateSha256 = '',
    bool? isWindows,
    Map<String, String>? environment,
  }) : isWindows = isWindows ?? Platform.isWindows,
       environment = environment ?? Platform.environment;

  final ReleaseProcessRunner processRunner;
  final bool requireTrustedSignature;
  final String expectedSignerCertificateSha256;
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
      }
      await verifyTrustedSignatures(stageRoot);
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
    final expectedSigner = expectedSignerCertificateSha256.trim().toLowerCase();
    if (!RegExp(r'^(?!0{64}$)[0-9a-f]{64}$').hasMatch(expectedSigner)) {
      throw StateError(
        'A reviewed Windows signer certificate SHA-256 is required.',
      );
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
      await _verifySignerIdentity(target, expectedSigner);
    }
  }

  Future<void> verifyUnsignedExecutables(String stageRoot) async {
    if (!isWindows) {
      throw StateError('Windows unsigned verification must run on Windows.');
    }
    final targets = _releaseTargets(stageRoot);
    if (targets.length != 3) {
      throw StateError(
        'Windows unsigned verification requires the CLI, GameCompat '
        'extractor, and launcher executable.',
      );
    }
    for (final target in targets) {
      await _verifyUnsignedExecutable(target);
    }
  }

  Future<void> _verifyUnsignedExecutable(String target) async {
    const targetEnvironmentName = 'TOPIAFORGE_AUTHENTICODE_TARGET';
    const script = r'''
$signature = Get-AuthenticodeSignature `
  -LiteralPath $env:TOPIAFORGE_AUTHENTICODE_TARGET
if (
  $signature.Status -ne
    [System.Management.Automation.SignatureStatus]::NotSigned
) { exit 2 }
if (
  $null -ne $signature.SignerCertificate -or
  $null -ne $signature.TimeStamperCertificate
) { exit 3 }
[Console]::Out.Write("unsigned")
''';
    final result = await processRunner.runResult(
      'powershell.exe',
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
      environment: {targetEnvironmentName: target},
    );
    if (result.exitCode != 0 || result.stdout.toString().trim() != 'unsigned') {
      throw StateError(
        'Windows executable is signed or has an invalid signature, but this '
        'technical dry-run requires an entirely unsigned package: '
        '${p.basename(target)}.',
      );
    }
  }

  Future<void> _verifySignerIdentity(
    String target,
    String expectedSigner,
  ) async {
    const targetEnvironmentName = 'TOPIAFORGE_AUTHENTICODE_TARGET';
    const script = r'''
$signature = Get-AuthenticodeSignature `
  -LiteralPath $env:TOPIAFORGE_AUTHENTICODE_TARGET
if (
  $signature.Status -ne
    [System.Management.Automation.SignatureStatus]::Valid
) { exit 2 }
if ($null -eq $signature.SignerCertificate) { exit 3 }
if ($null -eq $signature.TimeStamperCertificate) { exit 4 }
$algorithm = [System.Security.Cryptography.HashAlgorithmName]::SHA256
[Console]::Out.Write(
  $signature.SignerCertificate.GetCertHashString($algorithm).ToLowerInvariant()
)
''';
    final result = await processRunner.runResult(
      'powershell.exe',
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
      environment: {targetEnvironmentName: target},
    );
    final actualSigner = result.stdout.toString().trim().toLowerCase();
    if (result.exitCode != 0 ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(actualSigner) ||
        actualSigner != expectedSigner) {
      throw StateError(
        'Windows signer certificate does not match the reviewed release '
        'policy for ${p.basename(target)}.',
      );
    }
  }

  List<String> _releaseTargets(String stageRoot) => [
    p.join(stageRoot, 'topiaforge.exe'),
    p.join(stageRoot, 'TopiaForge.GameCompat.Extractor.exe'),
    p.join(stageRoot, 'launcher', 'topiaforge_launcher.exe'),
  ].where(FileSystemEntity.isFileSync).toList();

  String _timestampUrl() {
    final value = environment['WINDOWS_TIMESTAMP_URL']?.trim() ?? '';
    final uri = Uri.tryParse(value);
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host.isEmpty ||
        uri.userInfo.isNotEmpty ||
        uri.fragment.isNotEmpty) {
      throw StateError(
        'WINDOWS_TIMESTAMP_URL is mandatory and must be a credential-free '
        'HTTPS RFC 3161 endpoint.',
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
