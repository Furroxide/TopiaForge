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

  Future<List<String>> _runManagedPackageValidator(Directory packageRoot) =>
      runManagedPackageValidator(
        packageRoot,
        repositoryRoot: _repositoryRoot,
        searchRoots: _topiaForgeRootSeeds(_repositoryRoot),
      );
}
