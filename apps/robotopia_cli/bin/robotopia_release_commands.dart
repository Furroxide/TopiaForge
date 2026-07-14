part of 'robotopia.dart';

extension _RobotopiaReleaseCommands on _RobotopiaCli {
  Future<int> _release(List<String> args) async {
    return switch (args.firstOrNull) {
      'build-package' => _releaseBuildPackage(args.skip(1).toList()),
      'test-package' => _releaseTestPackage(args.skip(1).toList()),
      _ => throw UsageError(
        'Usage: robotopia release build-package|test-package ...',
      ),
    };
  }

  Future<int> _releaseBuildPackage(List<String> args) async {
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      throw StateError(
        'The QuantumWorks repository root was not found from ${Directory.current.path}.',
      );
    }
    final platform = _releasePlatform(args);
    final output = _option(args, '--output');
    if (output == null || output.trim().isEmpty) {
      throw UsageError(
        'Usage: robotopia release build-package --platform windows|linux|macos '
        '--output <dir> [--configuration Release] [--prebuilt-launcher <path>] '
        '[--prebuilt-cli <path>]',
      );
    }
    final builder = ReleasePackageBuilder(
      repositoryRoot: repoRoot,
      platform: platform,
      outputRoot: output,
      configuration: _option(args, '--configuration') ?? 'Release',
      prebuiltLauncher: _option(args, '--prebuilt-launcher') ?? '',
      prebuiltCli: _option(args, '--prebuilt-cli') ?? '',
    );
    stdout.writeln(await builder.build());
    return 0;
  }

  Future<int> _releaseTestPackage(List<String> args) async {
    final platform = _releasePlatform(args);
    final zip = _option(args, '--zip');
    if (zip == null || zip.trim().isEmpty) {
      throw UsageError(
        'Usage: robotopia release test-package --platform windows|linux|macos '
        '--zip <path> [--require-mac-universal]',
      );
    }
    await ReleasePackageValidator(
      platform: platform,
      zipPath: zip,
      requireMacUniversal: args.contains('--require-mac-universal'),
    ).validate();
    return 0;
  }

  ReleasePackagePlatform _releasePlatform(List<String> args) {
    final raw = _option(args, '--platform');
    if (raw == null || raw.trim().isEmpty) {
      throw UsageError(
        'Usage: robotopia release <command> --platform windows|linux|macos ...',
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
