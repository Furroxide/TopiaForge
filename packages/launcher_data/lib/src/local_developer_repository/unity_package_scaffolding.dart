part of '../local_developer_repository.dart';

/// Scaffolds standalone VPM packages with package-specific Unity assembly
/// identities. Two generated packages must be installable together without
/// colliding on the template's example asmdef names.
extension LocalDeveloperUnityPackageScaffolding on LocalDeveloperRepository {
  static const _templateAssemblyRoot = 'TopiaForge.Example';
  static const _templateMenuPath = 'TopiaForge/Example/Say Hello';

  Future<String> _createUnityPackage(
    String parentDirectory,
    String id,
    String name,
  ) async {
    final templateDir = Directory(
      p.join(
        _repositoryRoot.path,
        'templates',
        'TopiaForge.UnityPackageTemplate',
      ),
    );
    if (!templateDir.existsSync()) {
      throw StateError(
        'Unity package template not found at ${templateDir.path}.',
      );
    }

    final parent = Directory(parentDirectory)..createSync(recursive: true);
    final root = Directory(p.join(parent.path, _safeName(id)));
    if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError('Package already exists: ${root.path}');
    }
    final staging = Directory(
      p.join(
        parent.path,
        '.${_safeName(id)}.topiaforge-new-$pid-'
        '${DateTime.now().microsecondsSinceEpoch}',
      ),
    );

    try {
      _copyDirectory(templateDir, staging);
      _stampUnityPackage(staging, id, name);
      staging.renameSync(root.path);
      return root.path;
    } on Object {
      if (staging.existsSync()) {
        staging.deleteSync(recursive: true);
      }
      rethrow;
    }
  }

  void _stampUnityPackage(Directory root, String id, String name) {
    final packageFile = File(p.join(root.path, 'package.json'));
    if (!packageFile.existsSync()) {
      throw StateError('Unity package template has no package.json.');
    }
    final json =
        jsonDecode(
              utf8.decode(
                _readDeveloperFileBoundedSync(
                  packageFile,
                  maxBytes: _maxDeveloperManifestBytes,
                  label: 'Unity package.json',
                ),
              ),
            )
            as Map<String, Object?>;
    json['name'] = id;
    json['displayName'] = name.isEmpty ? id : name;
    _writeDeveloperTextAtomic(packageFile, _prettyJson(json));

    final assemblyRoot = _unityPackageAssemblyRoot(id);
    final templateSources =
        root
            .listSync(recursive: true)
            .whereType<File>()
            .where(
              (file) => const {
                '.asmdef',
                '.cs',
              }.contains(p.extension(file.path).toLowerCase()),
            )
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));
    for (final file in templateSources) {
      final text = utf8.decode(
        _readDeveloperFileBoundedSync(
          file,
          maxBytes: _maxDeveloperCatalogBytes,
          label: 'Unity package template source',
        ),
      );
      var updated = text.replaceAll(_templateAssemblyRoot, assemblyRoot);
      if (p.extension(file.path).toLowerCase() == '.cs') {
        updated = updated.replaceAll(
          jsonEncode(_templateMenuPath),
          jsonEncode('TopiaForge/Packages/$id/Say Hello'),
        );
      }
      _writeDeveloperTextAtomic(file, updated);
    }

    _renameAsmdef(
      root,
      p.join('Runtime', '$_templateAssemblyRoot.Runtime.asmdef'),
      p.join('Runtime', '$assemblyRoot.Runtime.asmdef'),
    );
    _renameAsmdef(
      root,
      p.join('Editor', '$_templateAssemblyRoot.Editor.asmdef'),
      p.join('Editor', '$assemblyRoot.Editor.asmdef'),
    );
    _restampUnityMetaGuids(root, id);
  }

  void _renameAsmdef(Directory root, String oldRelative, String newRelative) {
    for (final suffix in const ['', '.meta']) {
      final source = File(p.join(root.path, '$oldRelative$suffix'));
      if (!source.existsSync()) {
        throw StateError(
          'Unity package template is missing $oldRelative$suffix.',
        );
      }
      source.renameSync(p.join(root.path, '$newRelative$suffix'));
    }
  }

  void _restampUnityMetaGuids(Directory root, String id) {
    final metaFiles =
        root
            .listSync(recursive: true)
            .whereType<File>()
            .where((file) => file.path.endsWith('.meta'))
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));
    final replacements = <String, String>{};
    final newGuids = <String>{};
    final guidPattern = RegExp(
      r'^guid:\s*([0-9a-fA-F]{32})\s*$',
      multiLine: true,
    );
    for (final meta in metaFiles) {
      final text = utf8.decode(
        _readDeveloperFileBoundedSync(
          meta,
          maxBytes: _maxDeveloperCatalogBytes,
          label: 'Unity package template metadata',
        ),
      );
      final oldGuid = guidPattern.firstMatch(text)?.group(1)?.toLowerCase();
      if (oldGuid == null) {
        throw StateError(
          'Unity package template metadata has no valid GUID: ${meta.path}',
        );
      }
      if (replacements.containsKey(oldGuid)) {
        throw StateError(
          'Unity package template reuses metadata GUID $oldGuid.',
        );
      }
      final assetRelative = p.posix.joinAll(
        p.split(
          p.relative(
            meta.path.substring(0, meta.path.length - '.meta'.length),
            from: root.path,
          ),
        ),
      );
      final newGuid = sha256
          .convert(utf8.encode('$id\n$assetRelative'))
          .toString()
          .substring(0, 32);
      if (!newGuids.add(newGuid)) {
        throw StateError('Unity package GUID derivation collided for $id.');
      }
      replacements[oldGuid] = newGuid;
    }

    final referenceFiles =
        root
            .listSync(recursive: true)
            .whereType<File>()
            .where(
              (file) => const {
                '.asmdef',
                '.asmref',
                '.asset',
                '.controller',
                '.json',
                '.mat',
                '.meta',
                '.prefab',
                '.shader',
                '.unity',
                '.uxml',
                '.uss',
                '.xml',
                '.yaml',
                '.yml',
              }.contains(p.extension(file.path).toLowerCase()),
            )
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));
    for (final file in referenceFiles) {
      final original = utf8.decode(
        _readDeveloperFileBoundedSync(
          file,
          maxBytes: _maxDeveloperCatalogBytes,
          label: 'Unity package template asset',
        ),
      );
      var updated = original;
      replacements.forEach((oldGuid, newGuid) {
        updated = updated.replaceAll(
          RegExp(RegExp.escape(oldGuid), caseSensitive: false),
          newGuid,
        );
      });
      if (updated != original) {
        _writeDeveloperTextAtomic(file, updated);
      }
    }
  }

  /// Produces an injective, valid C# identifier path for ordinary VPM ids.
  /// Every segment gets a non-keyword prefix; punctuation is encoded rather
  /// than discarded so distinct package ids cannot collapse to one asmdef.
  String _unityPackageAssemblyRoot(String id) => id
      .split('.')
      .map((segment) {
        final output = StringBuffer('P_');
        if (segment.isEmpty) {
          return '${output}E_';
        }
        for (final rune in segment.runes) {
          final asciiAlphaNumeric =
              (rune >= 0x30 && rune <= 0x39) ||
              (rune >= 0x41 && rune <= 0x5a) ||
              (rune >= 0x61 && rune <= 0x7a);
          if (asciiAlphaNumeric) {
            output.writeCharCode(rune);
          } else if (rune == 0x2d) {
            output.write('_H_');
          } else if (rune == 0x5f) {
            output.write('_U_');
          } else {
            output.write('_X${rune.toRadixString(16)}_');
          }
        }
        return output.toString();
      })
      .join('.');
}
