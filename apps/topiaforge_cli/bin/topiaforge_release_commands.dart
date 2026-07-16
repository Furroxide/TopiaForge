part of 'topiaforge.dart';

extension _TopiaForgeReleaseCommands on _TopiaForgeCli {
  Future<int> _release(List<String> args) async {
    return switch (args.firstOrNull) {
      'build-package' => _releaseBuildPackage(args.skip(1).toList()),
      'build-sdk-payload' => _releaseBuildSdkPayload(args.skip(1).toList()),
      'test-package' => _releaseTestPackage(args.skip(1).toList()),
      'validate-policy' => _releaseValidatePolicy(args.skip(1).toList()),
      'build-metadata' => _releaseBuildMetadata(args.skip(1).toList()),
      'verify-metadata' => _releaseVerifyMetadata(args.skip(1).toList()),
      _ => throw UsageError(
        'Usage: topiaforge release build-package|build-sdk-payload|test-package|validate-policy|build-metadata|verify-metadata ...',
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
        '[--require-windows-signature] [--run-embedded-cli]',
      );
    }
    await ReleasePackageValidator(
      platform: platform,
      zipPath: zip,
      requireMacUniversal: args.contains('--require-mac-universal'),
      requireWindowsSignature: args.contains('--require-windows-signature'),
      requireMacTrust: args.contains('--require-macos-trust'),
      expectedMacTeamId: _option(args, '--expected-mac-team-id') ?? '',
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
