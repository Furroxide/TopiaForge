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

  /// Confirms [target] carries no Authenticode signature at all.
  ///
  /// The script reports the status rather than encoding a verdict in its exit
  /// code, because those are different questions. "This file is signed" is a
  /// policy violation the caller must not publish past; "I could not read this
  /// file" is a transient condition on Windows, where a freshly extracted
  /// executable is routinely held open by the antivirus scanner for a moment
  /// after it is written. Collapsing both into one failure reported a signing
  /// violation for a file that was never signed, on the path an unsigned
  /// release now depends on.
  Future<void> _verifyUnsignedExecutable(String target) async {
    const targetEnvironmentName = 'TOPIAFORGE_AUTHENTICODE_TARGET';
    const script = r'''
$ErrorActionPreference = "Stop"
try {
  $signature = Get-AuthenticodeSignature `
    -LiteralPath $env:TOPIAFORGE_AUTHENTICODE_TARGET
} catch {
  [Console]::Out.Write("unreadable:" + $_.Exception.GetType().Name)
  exit 0
}
$signer = if ($null -ne $signature.SignerCertificate) { "signer" } else { "none" }
$stamp = if ($null -ne $signature.TimeStamperCertificate) { "stamp" } else { "none" }
[Console]::Out.Write("$($signature.Status)|$signer|$stamp")
''';

    // A transient read is worth one short retry; a signed binary is not going
    // to become unsigned, so a real violation still fails on the first pass.
    const attempts = 3;
    var lastDetail = '';
    for (var attempt = 1; attempt <= attempts; attempt++) {
      final result = await processRunner.runResult(
        'powershell.exe',
        ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script],
        environment: {targetEnvironmentName: target},
      );
      final output = result.stdout.toString().trim();
      if (result.exitCode != 0) {
        lastDetail =
            'the Authenticode probe exited ${result.exitCode}: '
            '${result.stderr.toString().trim()}';
      } else if (output.startsWith('unreadable:')) {
        lastDetail = 'the file could not be read (${output.substring(11)})';
      } else {
        final fields = output.split('|');
        if (fields.length != 3) {
          lastDetail = 'the Authenticode probe returned "$output"';
        } else if (fields[0] == 'NotSigned' &&
            fields[1] == 'none' &&
            fields[2] == 'none') {
          return;
        } else if (fields[0] == 'UnknownError' && fields[1] == 'none') {
          // UnknownError with no signer is Windows saying it could not read the
          // file, not that it found something wrong with a signature. A signer
          // certificate is what separates the two, and the case below keeps it.
          lastDetail = 'the Authenticode status was UnknownError';
        } else {
          // A signer certificate is present, so this file really is signed -
          // an untrusted or self-signed certificate also lands here, reported
          // by Windows as UnknownError. Retrying cannot change it.
          throw StateError(
            'This dry-run requires an entirely unsigned package, but '
            '${p.basename(target)} carries a signature: status '
            '${fields[0]}, '
            '${fields[1] == 'signer' ? 'with' : 'without'} a signer '
            'certificate, '
            '${fields[2] == 'stamp' ? 'with' : 'without'} a timestamp.',
          );
        }
      }
    }

    throw StateError(
      'Could not determine whether ${p.basename(target)} is signed after '
      '$attempts attempts: $lastDetail. This is not a signing violation - the '
      'check never got an answer. Retry, and if it persists check whether an '
      'antivirus scanner is holding the extracted package open.',
    );
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
