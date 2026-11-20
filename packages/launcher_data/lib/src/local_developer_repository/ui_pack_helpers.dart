part of '../local_developer_repository.dart';

const _maxUiThemeDefinitionBytes = 64 * 1024;

typedef _PackedUiThemes = ({
  Map<String, Object?> catalogue,
  Map<String, List<int>> files,
});

extension _UiThemePackageContent on LocalDeveloperRepository {
  Future<_PackedUiThemes?> _prepareUiThemes(
    ModManifest manifest, {
    Directory? source,
    SafeZipArchive? archive,
  }) async {
    final catalogue = manifest.ui;
    if (catalogue == null) return null;

    final files = <String, List<int>>{};
    final paths = <String>{};
    final stagedEntries = <Map<String, Object?>>[];
    for (var index = 0; index < catalogue.themes.length; index++) {
      final theme = catalogue.themes[index];
      final field = 'ui.themes[$index]';
      final path = _portableDeveloperArchivePath(
        theme.definition,
        label: '$field.definition',
      );
      if (path != theme.definition ||
          p.posix.basename(path) == '.gitkeep' ||
          !paths.add(portableArchiveCollisionKey(path))) {
        throw StateError('$field.definition must be a distinct package file.');
      }

      final bytes = source != null
          ? _readUiThemeSource(source, path, field)
          : _readUiThemeArchive(archive!, path, field);
      final digest = sha256.convert(bytes).toString();
      final declaredDigest = theme.sha256;
      if ((archive != null && declaredDigest == null) ||
          (declaredDigest != null && declaredDigest.toLowerCase() != digest)) {
        throw StateError('$field.sha256 does not match the definition bytes.');
      }
      files[path] = bytes;
      stagedEntries.add({...theme.toJson(), 'sha256': digest});
    }

    final packedUi = <String, Object?>{
      ...catalogue.toJson(),
      'themes': stagedEntries,
    };
    await _validateStagedUiThemes({
      ...manifest.toJson(),
      'ui': packedUi,
    }, files);
    return (catalogue: packedUi, files: files);
  }

  Future<void> _validateStagedUiThemes(
    Map<String, Object?> manifest,
    Map<String, List<int>> files,
  ) async {
    final staged = Directory.systemTemp.createTempSync('topiaforge-ui-themes-');
    try {
      for (final entry in files.entries) {
        final file = File(p.join(staged.path, entry.key));
        file.parent.createSync(recursive: true);
        file.writeAsBytesSync(entry.value);
      }
      File(
        p.join(staged.path, 'topiaforge.mod.json'),
      ).writeAsStringSync('${_prettyJson(manifest)}\n');
      final validate = _uiThemePackageValidator;
      final errors = validate != null
          ? await validate(staged)
          : await runManagedPackageValidator(
              staged,
              repositoryRoot: _repositoryRoot,
              searchRoots: _topiaForgeRootSeeds(_repositoryRoot),
              resolveDotnet: _dotnetSdkResolver,
              uiThemesOnly: true,
            );
      if (errors.isNotEmpty) {
        throw StateError('UI theme validation failed: ${errors.join(' ')}');
      }
    } finally {
      staged.deleteSync(recursive: true);
    }
  }
}

List<int> _readUiThemeSource(Directory root, String path, String field) {
  var parent = root.path;
  if (FileSystemEntity.typeSync(parent, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('The theme package root must be an ordinary directory.');
  }
  final segments = path.split('/');
  for (final segment in segments.take(segments.length - 1)) {
    parent = p.join(parent, segment);
    if (FileSystemEntity.typeSync(parent, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('$field.definition passes through an unsafe directory.');
    }
  }
  final file = File(p.join(parent, segments.last));
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('$field.definition must name a regular package file.');
  }
  final resolved = file.resolveSymbolicLinksSync();
  if (!p.isWithin(root.resolveSymbolicLinksSync(), resolved)) {
    throw StateError('$field.definition escapes the package root.');
  }
  final bytes = _readDeveloperFileBoundedSync(
    File(resolved),
    maxBytes: _maxUiThemeDefinitionBytes,
    label: '$field.definition',
  );
  if (!p.equals(resolved, file.resolveSymbolicLinksSync())) {
    throw StateError('$field.definition changed while it was being read.');
  }
  return bytes;
}

List<int> _readUiThemeArchive(
  SafeZipArchive archive,
  String path,
  String field,
) {
  final entry = archive.entryNamed(path);
  if (entry == null || !entry.isFile) {
    throw StateError('$field.definition was not included in the package.');
  }
  return _readDeveloperArchiveEntryBounded(
    entry,
    maxBytes: _maxUiThemeDefinitionBytes,
    label: '$field.definition',
  );
}
