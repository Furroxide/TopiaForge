part of 'topiaforge.dart';

extension _TopiaForgeReleaseCommands on _TopiaForgeCli {
  Future<int> _release(List<String> args) async {
    return switch (args.firstOrNull) {
      'build-package' => _releaseBuildPackage(args.skip(1).toList()),
      'build-sdk-payload' => _releaseBuildSdkPayload(args.skip(1).toList()),
      'test-package' => _releaseTestPackage(args.skip(1).toList()),
      'validate-policy' => _releaseValidatePolicy(args.skip(1).toList()),
      'validate-readiness' => _releaseValidateReadiness(args.skip(1).toList()),
      'build-metadata' => _releaseBuildMetadata(args.skip(1).toList()),
      'verify-metadata' => _releaseVerifyMetadata(args.skip(1).toList()),
      'generate-update-key' => _releaseGenerateUpdateKey(args.skip(1).toList()),
      'build-update-metadata' => _releaseBuildUpdateMetadata(
        args.skip(1).toList(),
      ),
      'verify-update-metadata' => _releaseVerifyUpdateMetadata(
        args.skip(1).toList(),
      ),
      'build-platform-bundle' => _releaseBuildPlatformBundle(
        args.skip(1).toList(),
      ),
      'build-handoff' => _releaseBuildHandoff(args.skip(1).toList()),
      'verify-handoff' => _releaseVerifyHandoff(args.skip(1).toList()),
      _ => throw UsageError(
        'Usage: topiaforge release build-package|build-sdk-payload|test-package|validate-policy|validate-readiness|build-metadata|verify-metadata|generate-update-key|build-update-metadata|verify-update-metadata|build-platform-bundle|build-handoff|verify-handoff ...',
      ),
    };
  }

  /// Produces the small extracted-release developer payload used by the CI
  /// template matrix. It deliberately uses the same SDK writer, templates,
  /// and compiled CLI as a platform release, without requiring a launcher UI.
  Future<int> _releaseBuildSdkPayload(List<String> args) async {
    final repoRoot = _releaseRepositoryRoot();
    final output = _option(args, '--output');
    final cli = _option(args, '--cli');
    if (output == null ||
        output.trim().isEmpty ||
        cli == null ||
        cli.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release build-sdk-payload --output <empty-dir> '
        '--cli <compiled-cli> [--configuration Release]',
      );
    }
    final cliFile = File(p.normalize(p.absolute(cli)));
    if (!cliFile.existsSync()) {
      throw StateError('Compiled CLI was not found: ${cliFile.path}');
    }
    final destination = Directory(p.normalize(p.absolute(output)));
    final destinationPath = destination.absolute.path;
    final repository = Directory(repoRoot).absolute.path;
    if (p.equals(destinationPath, repository) ||
        p.isWithin(destinationPath, repository) ||
        p.equals(destinationPath, p.rootPrefix(destinationPath))) {
      throw StateError(
        'The SDK payload output cannot be the repository, one of its parents, or a filesystem root.',
      );
    }
    if (p.equals(destinationPath, cliFile.path) ||
        p.isWithin(destinationPath, cliFile.path)) {
      throw StateError(
        'The SDK payload output cannot contain the compiled CLI input.',
      );
    }
    if (FileSystemEntity.typeSync(destinationPath, followLinks: false) ==
        FileSystemEntityType.link) {
      throw StateError('The SDK payload output cannot be a symbolic link.');
    }
    if (destination.existsSync()) destination.deleteSync(recursive: true);
    destination.createSync(recursive: true);

    ReleaseSdkPayloadWriter(
      repositoryRoot: repoRoot,
      configuration: _option(args, '--configuration') ?? 'Release',
    ).write(destination.path);
    const files = ReleaseFileOps();
    for (final directory in const ['templates', 'tools', 'dist']) {
      final source = Directory(p.join(repoRoot, directory));
      if (source.existsSync()) {
        files.copyDirectory(
          source,
          Directory(p.join(destination.path, directory)),
        );
      } else {
        Directory(
          p.join(destination.path, directory),
        ).createSync(recursive: true);
      }
    }
    final executableName = p.extension(cliFile.path).toLowerCase() == '.exe'
        ? 'topiaforge.exe'
        : 'topiaforge';
    final executable = p.join(destination.path, executableName);
    cliFile.copySync(executable);
    await files.setExecutableBit(executable);
    const ReleaseSdkPayloadValidator().validate(destination.path);
    stdout.writeln(destination.path);
    return 0;
  }

  Future<int> _releaseValidatePolicy(List<String> args) async {
    final root = _releaseRepositoryRoot();
    final policy = TopiaForgeReleasePolicy.load(root);
    final version = _option(args, '--version') ?? policy.productVersion;
    final release = TopiaForgeReleaseCatalog.load(root).release(version);
    final issues = await const ReleasePolicyValidator().validate(
      policy: policy,
      release: release,
      allowUnresolvedPolicy: args.contains('--allow-unresolved-policy'),
      verifyArchiveHashes: !args.contains('--skip-archive-hashes'),
    );
    if (issues.isEmpty) {
      stdout.writeln('Release policy is internally consistent for $version.');
      return 0;
    }
    for (final issue in issues) {
      stderr.writeln('error: $issue');
    }
    return 1;
  }

  Future<int> _releaseValidateReadiness(List<String> args) async {
    final version = _option(args, '--version');
    final targetSha = _option(args, '--target-sha');
    if (version == null ||
        version.trim().isEmpty ||
        targetSha == null ||
        targetSha.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release validate-readiness '
        '--version <semver> --target-sha <40-character-sha>',
      );
    }
    final decision = await ReleaseReadinessDecision.loadAtGitSha(
      repositoryRoot: _releaseRepositoryRoot(),
      targetSha: targetSha,
      expectedReleaseVersion: version,
    );
    if (!decision.isReady) {
      for (final gate in decision.gates) {
        if (!gate.satisfiesRelease) {
          stderr.writeln(
            'error: Release readiness gate ${gate.id} is ${gate.status}.',
          );
        }
      }
      return 1;
    }
    stdout.writeln(
      const JsonEncoder.withIndent('  ').convert(decision.toPublicSummary()),
    );
    return 0;
  }

  Future<int> _releaseBuildMetadata(List<String> args) async {
    final root = _releaseRepositoryRoot();
    final version = _requiredReleaseOption(args, '--version');
    final targetSha = _requiredReleaseOption(args, '--target-sha');
    final assets = _requiredReleaseOption(args, '--assets');
    final output = _option(args, '--output') ?? assets;
    final result = await const TopiaForgeReleaseMetadataBuilder().build(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: assets,
      outputDirectory: output,
      allowUnresolvedPolicy: args.contains('--allow-unresolved-policy'),
    );
    stdout.writeln(result.bomPath);
    stdout.writeln(result.sbomPath);
    stdout.writeln(result.checksumsPath);
    return 0;
  }

  Future<int> _releaseVerifyMetadata(List<String> args) async {
    final root = _releaseRepositoryRoot();
    final assets = _requiredReleaseOption(args, '--assets');
    await const TopiaForgeReleaseMetadataBuilder().verify(
      repositoryRoot: root,
      version: _requiredReleaseOption(args, '--version'),
      targetSha: _requiredReleaseOption(args, '--target-sha'),
      assetsDirectory: assets,
      metadataDirectory: _option(args, '--metadata') ?? assets,
      allowUnresolvedPolicy: args.contains('--allow-unresolved-policy'),
    );
    stdout.writeln('Release metadata and checksums are valid.');
    return 0;
  }

  Future<int> _releaseGenerateUpdateKey(List<String> args) async {
    final privateOutput = _option(args, '--private-output');
    if (privateOutput == null || privateOutput.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release generate-update-key '
        '--private-output <owner-controlled-file>',
      );
    }
    await const ReleaseUpdateMetadataBuilder().generateKey(
      repositoryRoot: _releaseRepositoryRoot(),
      privateOutput: privateOutput,
    );
    stdout.writeln(
      'Generated release/update-keys.json and an owner-controlled private seed.',
    );
    return 0;
  }

  Future<int> _releaseBuildUpdateMetadata(List<String> args) async {
    final version = _requiredReleaseOption(args, '--version');
    final assets = _requiredReleaseOption(args, '--assets');
    final result = await const ReleaseUpdateMetadataBuilder().build(
      repositoryRoot: _releaseRepositoryRoot(),
      version: version,
      assetsDirectory: assets,
    );
    stdout.writeln(result.payload);
    stdout.writeln(result.signature);
    return 0;
  }

  Future<int> _releaseVerifyUpdateMetadata(List<String> args) async {
    final version = _requiredReleaseOption(args, '--version');
    final assets = _requiredReleaseOption(args, '--assets');
    await const ReleaseUpdateMetadataBuilder().verify(
      repositoryRoot: _releaseRepositoryRoot(),
      version: version,
      assetsDirectory: assets,
    );
    stdout.writeln('Signed launcher update metadata is valid.');
    return 0;
  }

  Future<int> _releaseBuildPlatformBundle(List<String> args) async {
    final version = _requiredHandoffOption(args, '--version');
    final targetSha = _requiredHandoffOption(args, '--target-sha');
    final platform = _requiredHandoffOption(args, '--platform');
    final archive = _requiredHandoffOption(args, '--archive');
    final canonicalSha = _requiredHandoffOption(
      args,
      '--canonical-ecosystem-sha256',
    );
    final qa = _requiredHandoffOption(args, '--qa');
    final output = _requiredHandoffOption(args, '--output');
    final evidence = <String, String>{};
    for (final value in _options(args, '--evidence')) {
      final separator = value.indexOf('=');
      if (separator <= 0 || separator == value.length - 1) {
        throw UsageError(
          'Each --evidence value must be <validation-name>=<sha256>.',
        );
      }
      final name = value.substring(0, separator);
      final digest = value.substring(separator + 1);
      if (!RegExp(r'^[a-z][a-z0-9-]*$').hasMatch(name) ||
          !RegExp(r'^[0-9a-f]{64}$').hasMatch(digest) ||
          evidence.containsKey(name)) {
        throw UsageError(
          'Evidence names must be unique lowercase identifiers and values '
          'must be lowercase SHA-256 digests.',
        );
      }
      evidence[name] = digest;
    }
    final path = await const TopiaForgeReleaseHandoff().buildPlatformBundle(
      repositoryRoot: _releaseRepositoryRoot(),
      version: version,
      targetSha: targetSha,
      platform: platform,
      archivePath: archive,
      canonicalEcosystemSha256: canonicalSha,
      evidenceSha256: evidence,
      qaPath: qa,
      outputPath: output,
    );
    stdout.writeln(path);
    return 0;
  }

  Future<int> _releaseBuildHandoff(List<String> args) async {
    final path = await const TopiaForgeReleaseHandoff().buildHandoff(
      repositoryRoot: _releaseRepositoryRoot(),
      version: _requiredHandoffOption(args, '--version'),
      targetSha: _requiredHandoffOption(args, '--target-sha'),
      assetsDirectory: _requiredHandoffOption(args, '--assets'),
      outputPath: _option(args, '--output'),
    );
    stdout.writeln(path);
    return 0;
  }

  Future<int> _releaseVerifyHandoff(List<String> args) async {
    final trustOutput = _option(args, '--trust-output');
    if (args.contains('--trust-output') &&
        (trustOutput == null || trustOutput.trim().isEmpty)) {
      throw UsageError('--trust-output requires a file path.');
    }
    final result = await const TopiaForgeReleaseHandoff().verify(
      repositoryRoot: _releaseRepositoryRoot(),
      version: _requiredHandoffOption(args, '--version'),
      targetSha: _requiredHandoffOption(args, '--target-sha'),
      assetsDirectory: _requiredHandoffOption(args, '--assets'),
      trustOutputPath: trustOutput,
      verifyEmbeddedEcosystem: args.contains('--verify-embedded-ecosystem'),
    );
    stdout.writeln(
      'Verified ${result.platformBundles.length} platform bundles for '
      '${result.handoff.version} at ${result.handoff.targetSha}.',
    );
    if (trustOutput != null) stdout.writeln(File(trustOutput).absolute.path);
    return 0;
  }

  String _releaseRepositoryRoot() {
    final root = _findRepoRoot();
    if (root == null) {
      throw StateError(
        'The TopiaForge repository root was not found from ${Directory.current.path}.',
      );
    }
    return root;
  }

  String _requiredReleaseOption(List<String> args, String option) {
    final value = _option(args, option);
    if (value == null || value.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release build-metadata|verify-metadata '
        '--version <semver> --target-sha <sha> --assets <dir> '
        '[--output|--metadata <dir>] [--allow-unresolved-policy]',
      );
    }
    return value;
  }

  String _requiredHandoffOption(List<String> args, String option) {
    final value = _option(args, option);
    if (value == null || value.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release build-platform-bundle|build-handoff|'
        'verify-handoff --version <semver> --target-sha <sha> ...',
      );
    }
    return value;
  }

  Future<int> _releaseBuildPackage(List<String> args) async {
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      throw StateError(
        'The TopiaForge repository root was not found from ${Directory.current.path}.',
      );
    }
    final platform = _releasePlatform(args);
    final output = _option(args, '--output');
    if (output == null || output.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release build-package --platform windows|linux|macos '
        '--output <dir> [--configuration Release] [--prebuilt-launcher <path>] '
        '[--prebuilt-cli <file-or-macos-pair-dir>] [--require-macos-signing] '
        '[--prebuilt-dist <dir>] [--require-windows-signing] [--skip-runtime-build]',
      );
    }
    final builder = ReleasePackageBuilder(
      repositoryRoot: repoRoot,
      platform: platform,
      outputRoot: output,
      configuration: _option(args, '--configuration') ?? 'Release',
      prebuiltLauncher: _option(args, '--prebuilt-launcher') ?? '',
      prebuiltCli: _option(args, '--prebuilt-cli') ?? '',
      prebuiltDist: _option(args, '--prebuilt-dist') ?? '',
      rebuildRuntimePayload: !args.contains('--skip-runtime-build'),
      requireMacSigning: args.contains('--require-macos-signing'),
      requireWindowsSigning: args.contains('--require-windows-signing'),
    );
    stdout.writeln(await builder.build());
    return 0;
  }

  Future<int> _releaseTestPackage(List<String> args) async {
    final platform = _releasePlatform(args);
    final zip = _option(args, '--zip');
    if (zip == null || zip.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release test-package --platform windows|linux|macos '
        '--zip <path> [--require-mac-universal] '
        '[--require-macos-trust] [--expected-mac-team-id id] '
        '[--require-windows-signature] '
        '[--require-windows-unsigned] '
        '[--expected-windows-signer-sha256 sha256] [--run-embedded-cli] '
        '[--expected-canonical-ecosystem-sha256 sha256] '
        '[--canonical-assets <dir>]',
      );
    }
    await ReleasePackageValidator(
      platform: platform,
      zipPath: zip,
      requireMacUniversal: args.contains('--require-mac-universal'),
      requireWindowsSignature: args.contains('--require-windows-signature'),
      requireWindowsUnsigned: args.contains('--require-windows-unsigned'),
      expectedWindowsSignerSha256:
          _option(args, '--expected-windows-signer-sha256') ?? '',
      requireMacTrust: args.contains('--require-macos-trust'),
      expectedMacTeamId: _option(args, '--expected-mac-team-id') ?? '',
      expectedCanonicalEcosystemSha256:
          _option(args, '--expected-canonical-ecosystem-sha256') ?? '',
      canonicalAssetsDirectory: _option(args, '--canonical-assets') ?? '',
      runCliSmoke: args.contains('--run-embedded-cli'),
    ).validate();
    return 0;
  }

  ReleasePackagePlatform _releasePlatform(List<String> args) {
    final raw = _option(args, '--platform');
    if (raw == null || raw.trim().isEmpty) {
      throw UsageError(
        'Usage: topiaforge release <command> --platform windows|linux|macos ...',
      );
    }
    try {
      return ReleasePackagePlatform.parse(raw);
    } on ArgumentError {
      throw UsageError(
        'Invalid platform "$raw". Expected windows, linux, or macos.',
      );
    }
  }
}
