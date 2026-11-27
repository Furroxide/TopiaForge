part of '../local_developer_repository.dart';

/// After a restore: what NuGet resolved for each SDK package of a mod
/// project, as its lock file records it, against the SDK pin.
extension LocalDeveloperRestoredSdkCheck on LocalDeveloperRepository {
  static const _docs = 'https://docs.topiaforge.dev/diagnostics';

  /// The files MSBuild imports into a project by their names alone, and so
  /// the likeliest to set an SDK package's version from outside the pin.
  static const _implicitImports = [
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
  ];

  /// One blocking issue for each SDK package that a restore of one of
  /// [projects] resolved at another version than [sdk]'s, or did not resolve
  /// though the project is pinned to it (TF1102), and one for each project
  /// whose lock file is missing, cannot be read, or was not written by its
  /// restore, as the snapshot in [snapshots] taken just before it tells
  /// (TF1103).
  Future<List<LauncherIssue>> _checkRestoredSdkPackages(
    String root,
    SdkReferencePack sdk,
    List<File> projects,
    Map<String, NuGetLockFileSnapshot> snapshots,
  ) async {
    final modulePackages = await _sdkModulePackages(root, sdk);
    final sdkPackages = sdk.packages.map((package) => package.id).toSet();
    final issues = <LauncherIssue>[];
    for (final project in projects) {
      final name = p.basename(project.path);
      final text = utf8.decode(
        _readDeveloperFileBoundedSync(
          project,
          maxBytes: _maxDeveloperCatalogBytes,
          label: name,
        ),
      );
      final pinned = _pinnedSdkPackages(project, text, sdk, modulePackages);
      final location = nuGetLockFileOf(project, projectText: text);
      final lockFile = location.file;
      final lockName = p.relative(lockFile.path, from: project.parent.path);
      if (FileSystemEntity.typeSync(lockFile.path, followLinks: false) ==
          FileSystemEntityType.notFound) {
        final lookedFor = switch (location.source) {
          NuGetLockFileSource.recorded =>
            '$lockName, where obj/project.assets.json records that '
                'NuGetLockFilePath put it',
          NuGetLockFileSource.declared =>
            '$lockName, where $name declares that NuGetLockFilePath puts it',
          NuGetLockFileSource.projectNamed || NuGetLockFileSource.beside =>
            '${projectNamedNuGetLockFileName(project)} and $lockName beside '
                '$name, and obj/project.assets.json recorded no other path',
        };
        // NuGet writes the lock file where NuGetLockFilePath says, so a path
        // the project declares where NuGet wrote nothing is not the one that
        // applies.
        final next = location.source == NuGetLockFileSource.declared
            ? 'NuGet did not write it there, so a later setting, a condition '
                  'or an import sets NuGetLockFilePath to another path, or to '
                  'none. ${_applyDeclaredLockFilePath(name)}'
            : 'Keep RestorePackagesWithLockFile on, as the template sets it, '
                  'and run `topiaforge restore` again. If NuGetLockFilePath '
                  'moves the lock file, keep obj beside $name, where the '
                  'restore records that path, or declare NuGetLockFilePath in '
                  '$name itself as a literal path.';
        issues.add(
          _restoreIssue(
            'TF1103: No NuGet lock file was found for $name after its '
                'restore: restore looked for $lookedFor. So the SDK versions '
                'NuGet resolved cannot be checked. $next',
            'TF1103',
          ),
        );
        continue;
      }
      // A lock file this restore did not write says nothing about what it
      // resolved, however well it matches the pin.
      if (!snapshots[project.path]!.wrote(lockFile)) {
        final cause = switch (location.source) {
          NuGetLockFileSource.recorded =>
            'the obj/project.assets.json beside $name that names it is left '
                'from an earlier restore, so delete that obj folder',
          NuGetLockFileSource.declared =>
            '$name declares that NuGetLockFilePath puts it there, but a later '
                'setting, a condition or an import sets another path, or none',
          NuGetLockFileSource.projectNamed || NuGetLockFileSource.beside =>
            'something outside $name sets NuGetLockFilePath, and obj is not '
                'beside $name to record where',
        };
        final next = location.source == NuGetLockFileSource.declared
            ? _applyDeclaredLockFilePath(name)
            : 'Keep obj beside $name, where the restore records where '
                  'NuGetLockFilePath puts the lock file, or declare '
                  'NuGetLockFilePath in $name itself as a literal path, then '
                  'run `topiaforge restore` again.';
        issues.add(
          _restoreIssue(
            'TF1103: $lockName, the NuGet lock file found for $name, was not '
                'written by its restore, so it does not say which SDK '
                'versions NuGet resolved: $cause. $next',
            'TF1103',
          ),
        );
        continue;
      }
      final List<SdkPinMismatch> mismatches;
      try {
        mismatches = sdkPinMismatches(
          jsonDecode(
            utf8.decode(
              _readDeveloperFileBoundedSync(
                lockFile,
                maxBytes: maximumNuGetLockBytes,
                label: 'The NuGet lock file of $name',
              ),
            ),
          ),
          pinned: pinned,
          sdkPackages: sdkPackages,
          version: sdk.version,
        );
      } on Object catch (error) {
        final reason = switch (error) {
          FormatException(:final message) => message,
          StateError(:final message) => message,
          FileSystemException(:final message) => message,
          _ => 'it could not be read',
        };
        issues.add(
          _restoreIssue(
            'TF1103: The NuGet lock file of $name, $lockName, cannot be '
                'read ($reason), so the SDK versions NuGet resolved cannot be '
                'checked. Run `topiaforge restore` again.',
            'TF1103',
          ),
        );
        continue;
      }
      for (final mismatch in mismatches) {
        final where = _importsNaming(project, root, mismatch.packageId);
        // The three implicitly imported files come first: they are where
        // such an item usually lives, even when none names the package in
        // plain text.
        final cause = where.isEmpty
            ? 'a ${_implicitImports.take(2).join(', ')} or '
                  '${_implicitImports.last} above $name, a property or item '
                  'expression in $name, another imported .props or .targets '
                  'file, or a target'
            : '${where.join(' or ')}, which names it';
        issues.add(
          _restoreIssue(
            mismatch.resolved.isEmpty
                ? 'TF1102: NuGet resolved no ${mismatch.packageId} for $name, '
                      'though $name is pinned to it at ${sdk.version}. '
                      'Something outside its PackageReference removes it: '
                      '$cause. Remove that, then run `topiaforge restore` '
                      'again.'
                : 'TF1102: NuGet resolved ${mismatch.packageId} '
                      '${mismatch.resolved.join(' and ')} for $name, but the '
                      'SDK is pinned to ${sdk.version}. Something outside its '
                      'PackageReference sets that: $cause. Make it use '
                      '${sdk.version}, or remove it, then run '
                      '`topiaforge restore` again.',
            'TF1102',
            subject: mismatch.packageId,
          ),
        );
      }
    }
    return issues;
  }

  LauncherIssue _restoreIssue(String message, String code, {String? subject}) =>
      LauncherIssue(
        severity: IssueSeverity.error,
        message: '$message See $_docs/$code',
        subjectId: subject,
      );

  /// The way past TF1103 when NuGet did not follow the NuGetLockFilePath
  /// that [name], a project file, declares: a later setting, a condition or
  /// an import sets another path, or none, so declaring it again changes
  /// nothing.
  String _applyDeclaredLockFilePath(String name) =>
      'Make the NuGetLockFilePath that $name declares the one that applies, '
      'or keep obj beside $name, where the restore records the path NuGet '
      'used, then run `topiaforge restore` again.';

  /// The implicitly imported files from [project]'s folder up that name
  /// [packageId], relative to the mod [root] when they are inside it.
  List<String> _importsNaming(File project, String root, String packageId) {
    final found = <String>[];
    final id = packageId.toLowerCase();
    for (var folder = project.absolute.parent; ; folder = folder.parent) {
      for (final name in _implicitImports) {
        final file = File(p.join(folder.path, name));
        try {
          if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
                  FileSystemEntityType.file &&
              file.lengthSync() <= _maxDeveloperCatalogBytes &&
              file.readAsStringSync().toLowerCase().contains(id)) {
            found.add(
              p.isWithin(root, file.path)
                  ? p.relative(file.path, from: root)
                  : file.path,
            );
          }
        } on Object {
          // Unreadable here: the message names the likely causes instead.
        }
      }
      if (folder.parent.path == folder.path) return found;
    }
  }
}
