part of '../local_launcher_repository.dart';

extension _PackageMetadataValidation on LocalLauncherRepository {
  Future<void> _validatePackageMetadataBeforeCommit(
    Directory packageRoot,
  ) async {
    final errors = _packageMetadataValidator != null
        ? await _packageMetadataValidator(packageRoot)
        : await _runManagedPackageValidator(packageRoot);
    if (errors.isEmpty) return;
    throw StateError(
      'Managed package metadata validation failed: ${errors.join(' ')}',
    );
  }

  Future<List<String>> _runManagedPackageValidator(
    Directory packageRoot,
  ) async {
    final validator = _resolveManagedPackageValidator();
    final hostRoot = _findValidatorHostRoot(validator);
    final dotnet = await resolveRepositoryDotnetSdk(hostRoot);
    try {
      final result = await runBoundedProcess(
        dotnet.executable,
        [validator.path, packageRoot.path],
        workingDirectory: hostRoot.path,
        runInShell: false,
        timeout: const Duration(seconds: 30),
        maxStdoutBytes: 1024 * 1024,
        maxStderrBytes: 4 * 1024 * 1024,
      );
      if (result.exitCode == 0) return const [];
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
    }
  }

  File _resolveManagedPackageValidator() {
    final override = Platform.environment['TOPIAFORGE_PACKAGE_VALIDATOR_PATH'];
    final candidates = <File>[
      if (override != null && override.trim().isNotEmpty)
        File(p.normalize(p.absolute(override))),
      File(
        p.join(
          _repositoryRoot.path,
          'tools',
          'package-validator',
          'TopiaForge.ModPackageValidator.dll',
        ),
      ),
      File(
        p.join(
          _repositoryRoot.path,
          'src',
          'TopiaForge.ModPackageValidator',
          'bin',
          'Release',
          'net10.0',
          'TopiaForge.ModPackageValidator.dll',
        ),
      ),
      File(
        p.join(
          _repositoryRoot.path,
          'dist',
          'TopiaForge',
          'tools',
          'package-validator',
          'TopiaForge.ModPackageValidator.dll',
        ),
      ),
    ];
    for (final seed in _topiaForgeRootSeeds(_repositoryRoot)) {
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
      'TopiaForge release before installing mods.',
    );
  }
}

Directory _findValidatorHostRoot(File validator) {
  var current = validator.parent.absolute;
  while (true) {
    final global = File(p.join(current.path, 'global.json'));
    if (FileSystemEntity.typeSync(global.path, followLinks: false) ==
        FileSystemEntityType.file) {
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
