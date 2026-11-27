part of '../local_developer_repository.dart';

const _maxUiThemeDefinitionBytes = 64 * 1024;
const _maxUiBundleBytes = 32 * 1024 * 1024;
const _maxUiPackageBundleBytes = 64 * 1024 * 1024;
const _maxUiReceiptBytes = 512 * 1024;

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
    final paths = <String, (String, _UiThemeFileKind)>{};
    final stagedEntries = <Map<String, Object?>>[];
    final resources = <_UiThemeResource>[];
    for (var index = 0; index < catalogue.themes.length; index++) {
      final theme = catalogue.themes[index];
      final field = 'ui.themes[$index]';
      final path = theme.definition;
      final definition = _UiThemeResource(
        path,
        '$field.definition',
        _UiThemeFileKind.definition,
      );
      _addUiThemePath(definition, paths);
      final bytes = definition.read(source: source, archive: archive);
      final hasImages = _collectUiThemeResources(bytes, field, resources);
      final willEnrich = source != null && hasImages;
      final digest = sha256.convert(bytes).toString();
      final declaredDigest = theme.sha256;
      if ((archive != null && declaredDigest == null) ||
          (!willEnrich &&
              declaredDigest != null &&
              declaredDigest.toLowerCase() != digest)) {
        throw StateError('$field.sha256 does not match the definition bytes.');
      }
      files[path] = bytes;
      stagedEntries.add({...theme.toJson(), if (!willEnrich) 'sha256': digest});
    }

    var encodedBundleBytes = 0;
    var resourceBytes = 0;
    for (final resource in resources) {
      if (!_addUiThemePath(resource, paths)) continue;
      final bundle = resource.kind == _UiThemeFileKind.bundle;
      final bytes = resource.read(
        source: source,
        archive: archive,
        remainingBytes: bundle
            ? _maxUiPackageBundleBytes - encodedBundleBytes
            : _maxDeveloperArchiveExpandedBytes - resourceBytes,
      );
      if (bundle) encodedBundleBytes += bytes.length;
      resourceBytes += bytes.length;
      if (resourceBytes > _maxDeveloperArchiveExpandedBytes) {
        throw StateError('UI theme files exceed the package size limit.');
      }
      files[resource.path] = bytes;
    }

    final packedUi = <String, Object?>{
      ...catalogue.toJson(),
      'themes': stagedEntries,
    };
    return _finishStagedUiThemes(
      {...manifest.toJson(), 'ui': packedUi},
      files,
      prepare: source != null,
    );
  }

  Future<_PackedUiThemes> _finishStagedUiThemes(
    Map<String, Object?> manifest,
    Map<String, List<int>> files, {
    required bool prepare,
  }) async {
    final staged = Directory.systemTemp.createTempSync('topiaforge-ui-themes-');
    try {
      for (final entry in files.entries) {
        final file = File(p.join(staged.path, entry.key));
        file.parent.createSync(recursive: true);
        file.writeAsBytesSync(entry.value);
      }
      final manifestFile = File(p.join(staged.path, 'topiaforge.mod.json'));
      manifestFile.writeAsStringSync('${_prettyJson(manifest)}\n');
      final validate = _uiThemePackageValidator;
      final errors = validate != null
          ? await validate(staged)
          : await runManagedPackageValidator(
              staged,
              repositoryRoot: _repositoryRoot,
              searchRoots: _topiaForgeRootSeeds(_repositoryRoot),
              resolveDotnet: _dotnetSdkResolver,
              mode: prepare
                  ? ManagedPackageValidation.prepareThemes
                  : ManagedPackageValidation.themes,
            );
      if (errors.isNotEmpty) {
        throw StateError('UI theme validation failed: ${errors.join(' ')}');
      }
      final catalogue = ModManifest.fromJson(
        _decodeDeveloperManifest(
          _readDeveloperFileBoundedSync(
            manifestFile,
            maxBytes: _maxDeveloperManifestBytes,
            label: 'topiaforge.mod.json',
          ),
        ),
      ).ui!;
      if (prepare) {
        for (final (index, entry) in catalogue.themes.indexed) {
          final field = 'ui.themes[$index]';
          if (!files.containsKey(entry.definition)) {
            throw StateError('$field.definition was not staged.');
          }
          final definition = _readUiThemeSource(
            staged,
            entry.definition,
            '$field.definition',
            _maxUiThemeDefinitionBytes,
          );
          if (entry.sha256?.toLowerCase() !=
              sha256.convert(definition).toString()) {
            throw StateError(
              '$field.sha256 does not match the packaged bytes.',
            );
          }
          files[entry.definition] = definition;
        }
      }
      return (catalogue: catalogue.toJson(), files: files);
    } finally {
      staged.deleteSync(recursive: true);
    }
  }
}

List<int> _readUiThemeSource(
  Directory root,
  String path,
  String field,
  int maximumBytes,
) {
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
      throw StateError('$field passes through an unsafe directory.');
    }
  }
  final file = File(p.join(parent, segments.last));
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('$field must name a regular package file.');
  }
  final resolved = file.resolveSymbolicLinksSync();
  if (!p.isWithin(root.resolveSymbolicLinksSync(), resolved)) {
    throw StateError('$field escapes the package root.');
  }
  final bytes = _readDeveloperFileBoundedSync(
    File(resolved),
    maxBytes: maximumBytes,
    label: field,
  );
  if (!p.equals(resolved, file.resolveSymbolicLinksSync())) {
    throw StateError('$field changed while it was being read.');
  }
  return bytes;
}

List<int> _readUiThemeArchive(
  SafeZipArchive archive,
  String path,
  String field,
  int maximumBytes,
) {
  final entry = archive.entryNamed(path);
  if (entry == null || !entry.isFile) {
    throw StateError('$field was not included in the package.');
  }
  return _readDeveloperArchiveEntryBounded(
    entry,
    maxBytes: maximumBytes,
    label: field,
  );
}

enum _UiThemeFileKind {
  definition(_maxUiThemeDefinitionBytes),
  bundle(_maxUiBundleBytes),
  receipt(_maxUiReceiptBytes),
  license(_maxDeveloperArchiveEntryBytes);

  const _UiThemeFileKind(this.maximumBytes);
  final int maximumBytes;
}

class _UiThemeResource {
  const _UiThemeResource(this.path, this.field, this.kind);

  final String path;
  final String field;
  final _UiThemeFileKind kind;

  List<int> read({
    Directory? source,
    SafeZipArchive? archive,
    int? remainingBytes,
  }) {
    final limit = remainingBytes != null && remainingBytes < kind.maximumBytes
        ? remainingBytes
        : kind.maximumBytes;
    return source != null
        ? _readUiThemeSource(source, path, field, limit)
        : _readUiThemeArchive(archive!, path, field, limit);
  }
}

bool _addUiThemePath(
  _UiThemeResource file,
  Map<String, (String, _UiThemeFileKind)> seen,
) {
  final path = _portableDeveloperArchivePath(file.path, label: file.field);
  final key = portableArchiveCollisionKey(path);
  if (path != file.path ||
      p.posix.basename(key).toLowerCase() == '.gitkeep' ||
      key.toLowerCase() == 'topiaforge.mod.json' ||
      key.toLowerCase() == 'topiaforge.install.json') {
    throw StateError('${file.field} must name a distinct package file.');
  }
  final previous = seen[key];
  if (previous == null) {
    seen[key] = (path, file.kind);
    return true;
  }
  if (previous != (path, file.kind) ||
      file.kind == _UiThemeFileKind.definition) {
    throw StateError('${file.field} collides with another package file.');
  }
  return false;
}

bool _collectUiThemeResources(
  List<int> definition,
  String field,
  List<_UiThemeResource> files,
) {
  // Gather paths for the managed parser's input. Only that parser interprets
  // themes, validates declarations, and supplies missing image facts.
  final Object? decoded;
  try {
    final text = utf8.decode(definition);
    rejectDuplicateJsonProperties(text, label: '$field.definition');
    decoded = jsonDecode(text);
  } on FormatException {
    return false;
  }
  if (decoded is! Map<String, Object?>) return false;
  for (final (key, maximum) in const [('images', 64), ('fonts', 9)]) {
    final entries = decoded[key];
    if (entries is! List<Object?> || entries.length > maximum) continue;
    for (final (index, entry) in entries.indexed) {
      if (entry is! Map<String, Object?>) continue;
      final logical = '$field.$key[$index]';
      final bundle = entry['bundlePath'];
      if (bundle is String && utf8.encode(bundle).length <= 256) {
        files.add(
          _UiThemeResource(
            bundle,
            '$logical.bundlePath',
            _UiThemeFileKind.bundle,
          ),
        );
        final receipt = sha256.convert(utf8.encode(bundle));
        files.add(
          _UiThemeResource(
            'ui/receipts/$receipt.json',
            '$logical.bundleReceipt',
            _UiThemeFileKind.receipt,
          ),
        );
      }
      final license = entry['licensePath'];
      if (license is String && utf8.encode(license).length <= 256) {
        files.add(
          _UiThemeResource(
            license,
            '$logical.licensePath',
            _UiThemeFileKind.license,
          ),
        );
      }
    }
  }
  return decoded['images'] is List<Object?> &&
      (decoded['images']! as List<Object?>).isNotEmpty;
}
