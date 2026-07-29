part of 'topiaforge.dart';

/// Bridges the Dart package reader to the shared, non-executing C# metadata
/// validator carried by source builds and extracted releases.
extension _TopiaForgeManagedPackageValidation on _TopiaForgeCli {
  Future<List<String>> _managedPackageValidationErrors(
    String packagePath, {
    String? dotnetExecutable,
    String? dotnetRoot,
  }) async {
    final validator = _managedPackageValidator();
    final hostRoot = dotnetRoot == null
        ? _findPinnedDotnetRoot(validator)
        : Directory(p.normalize(p.absolute(dotnetRoot)));
    final dotnet =
        dotnetExecutable ??
        (await resolveRepositoryDotnetSdk(hostRoot)).executable;

    final bytes = readBoundedRegularFileSync(
      File(packagePath),
      maxBytes: CliFileLimits.package,
    );
    final archive = SafeZipArchive.decode(bytes, label: 'Package');
    final extracted = Directory.systemTemp.createTempSync(
      'topiaforge-package-validation-',
    );
    try {
      archive.extractTo(extracted);
      final result = await runBoundedProcess(
        dotnet,
        [validator.path, extracted.path],
        workingDirectory: hostRoot.path,
        runInShell: false,
        timeout: const Duration(seconds: 30),
        maxStdoutBytes: 1024 * 1024,
        maxStderrBytes: 4 * 1024 * 1024,
      );
      if (result.exitCode == 0) {
        return const [];
      }
      final details = <String>[
        ...const LineSplitter().convert(result.stderr),
        ...const LineSplitter().convert(result.stdout),
      ].map((line) => line.trim()).where((line) => line.isNotEmpty).toList();
      return details.isEmpty
          ? const [
              'TFPKG160: managed package metadata validation failed without details.',
            ]
          : details;
    } on BoundedProcessException catch (error) {
      return ['TFPKG161: managed package validator could not complete: $error'];
    } finally {
      if (extracted.existsSync()) extracted.deleteSync(recursive: true);
    }
  }

  File _managedPackageValidator() {
    final override = Platform.environment['TOPIAFORGE_PACKAGE_VALIDATOR_PATH'];
    final candidates = <File>[
      if (override != null && override.trim().isNotEmpty)
        File(p.normalize(p.absolute(override))),
    ];
    final sourceRoot = _findRepoRoot();
    if (sourceRoot != null) {
      candidates.add(
        File(
          p.join(
            sourceRoot,
            'src',
            'TopiaForge.ModPackageValidator',
            'bin',
            'Release',
            'net10.0',
            'TopiaForge.ModPackageValidator.dll',
          ),
        ),
      );
    }

    final executableDirectory = File(
      Platform.resolvedExecutable,
    ).absolute.parent;
    for (final seed in _managedValidatorSeeds(executableDirectory)) {
      var current = seed.absolute;
      for (var depth = 0; depth < 8; depth++) {
        candidates.add(
          File(
            p.join(
              current.path,
              'tools',
              'package-validator',
              'TopiaForge.ModPackageValidator.dll',
            ),
          ),
        );
        final parent = current.parent;
        if (parent.path == current.path) break;
        current = parent;
      }
    }

    for (final candidate in candidates) {
      if (FileSystemEntity.typeSync(candidate.path, followLinks: false) ==
          FileSystemEntityType.file) {
        return candidate.absolute;
      }
    }
    throw StateError(
      'TFPKG150: the managed package validator is missing. Repair the '
      'TopiaForge release, or build TopiaForge.ModPackageValidator in Release.',
    );
  }

  Iterable<Directory> _managedValidatorSeeds(
    Directory executableDirectory,
  ) sync* {
    yield executableDirectory;
    if (p.basename(executableDirectory.path) == 'MacOS' &&
        p.basename(executableDirectory.parent.path) == 'Contents') {
      yield Directory(
        p.join(executableDirectory.parent.path, 'Resources', 'TopiaForge'),
      );
    }
  }

  Directory _findPinnedDotnetRoot(File validator) {
    var current = validator.parent.absolute;
    while (true) {
      if (File(p.join(current.path, 'global.json')).existsSync()) {
        return current;
      }
      final parent = current.parent;
      if (parent.path == current.path) {
        throw StateError(
          'TFPKG151: global.json is missing beside the package validator. '
          'Repair the TopiaForge release.',
        );
      }
      current = parent;
    }
  }
}
