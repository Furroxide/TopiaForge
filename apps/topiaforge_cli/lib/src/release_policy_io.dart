part of 'release_policy.dart';

class BepInExProvenanceVerifier {
  const BepInExProvenanceVerifier();

  Future<void> verify(String repositoryRoot) async {
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final issues = <String>[];
    await _validateReleaseProvenance(
      policy,
      repositoryRoot,
      issues,
      verifyArchiveHashes: true,
    );
    if (issues.isNotEmpty) {
      throw StateError(
        'BepInEx provenance validation failed:\n- ${issues.join('\n- ')}',
      );
    }
  }
}

Future<void> _validateReleaseProvenance(
  TopiaForgeReleasePolicy policy,
  String root,
  List<String> issues, {
  required bool verifyArchiveHashes,
}) async {
  for (final relative in policy.provenanceFiles) {
    final provenance = _readObject(File(p.join(root, relative)));
    if (provenance['schemaVersion'] != 2 ||
        provenance['bundledAssets'] is! List ||
        provenance['correspondingSource'] is! List) {
      issues.add('$relative is not a supported provenance record.');
      continue;
    }
    for (final value in provenance['bundledAssets'] as List) {
      final asset = _object(value, 'provenance asset');
      final uri = Uri.tryParse(asset['sourceUrl'] as String? ?? '');
      final sha = asset['sha256'];
      final file = File(
        p.join(root, 'third_party', 'BepInEx', asset['file'] as String? ?? ''),
      );
      if (uri == null || uri.scheme != 'https' || uri.userInfo.isNotEmpty) {
        issues.add('$relative contains a non-HTTPS provenance URL.');
      }
      if (!_isSha256(sha) ||
          !file.existsSync() ||
          file.lengthSync() != asset['size']) {
        issues.add('$relative does not match ${file.path}.');
        continue;
      }
      if (verifyArchiveHashes && await _sha256File(file) != sha) {
        issues.add('${file.path} does not match its provenance SHA-256.');
      }
      final extracted = Directory(
        p.join(
          root,
          'third_party',
          'BepInEx',
          asset['extractedDirectory'] as String? ?? '',
        ),
      );
      if (!extracted.existsSync()) {
        issues.add(
          'Extracted provenance directory is missing: ${extracted.path}.',
        );
      } else {
        await _validateExtractedTree(
          root,
          asset,
          extracted,
          issues,
          verifyHashes: verifyArchiveHashes,
          verifyModes: !Platform.isWindows,
        );
      }
    }
    for (final value in provenance['correspondingSource'] as List) {
      final source = _object(value, 'corresponding source');
      final file = File(
        p.join(root, 'third_party', 'BepInEx', source['file'] as String? ?? ''),
      );
      final uri = Uri.tryParse(source['sourceUrl'] as String? ?? '');
      final license = File(
        p.join(
          root,
          'third_party',
          'BepInEx',
          source['licenseFile'] as String? ?? '',
        ),
      );
      if (source['commit'] != policy.unityDoorstopCommit ||
          uri == null ||
          uri.scheme != 'https' ||
          uri.userInfo.isNotEmpty ||
          !_isSha256(source['sha256']) ||
          !file.existsSync() ||
          file.lengthSync() != source['size'] ||
          !license.existsSync()) {
        issues.add('$relative has invalid corresponding-source provenance.');
        continue;
      }
      if (verifyArchiveHashes && await _sha256File(file) != source['sha256']) {
        issues.add('${file.path} does not match its provenance SHA-256.');
      }
    }
  }
}

Future<void> _validateExtractedTree(
  String root,
  Map<String, Object?> asset,
  Directory extracted,
  List<String> issues, {
  required bool verifyHashes,
  required bool verifyModes,
}) async {
  final treePath = p.join(
    root,
    'third_party',
    'BepInEx',
    asset['treeManifest'] as String? ?? '',
  );
  final tree = _readObject(File(treePath));
  if (tree['schemaVersion'] != 2) {
    issues.add('$treePath must use schemaVersion 2.');
    return;
  }
  final expectedDirectories = _stringList(
    tree['directories'],
    '$treePath directories',
  ).toSet();
  final expectedFiles = _object(tree['files'], '$treePath files');
  final actualDirectories = <String>{};
  final actualFiles = <String>{};
  for (final entity in extracted.listSync(
    recursive: true,
    followLinks: false,
  )) {
    final relative = p
        .relative(entity.path, from: extracted.path)
        .replaceAll('\\', '/');
    final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
    if (type == FileSystemEntityType.link) {
      issues.add('BepInEx extracted tree contains a link: $relative.');
      continue;
    }
    if (type == FileSystemEntityType.directory) {
      actualDirectories.add(relative);
      continue;
    }
    if (type != FileSystemEntityType.file) {
      issues.add('BepInEx extracted tree contains a special entry: $relative.');
      continue;
    }
    actualFiles.add(relative);
    final expected = expectedFiles[relative];
    if (expected is! Map) {
      continue;
    }
    final metadata = _object(expected, '$treePath $relative');
    final stat = File(entity.path).statSync();
    final mode = (stat.mode & 0x1ff).toRadixString(8).padLeft(4, '0');
    if (stat.size != metadata['size']) {
      issues.add('BepInEx extracted entry size drifted: $relative.');
    }
    // Windows filesystems do not expose the source archive's POSIX mode bits.
    // Byte length is mandatory on every platform. SHA-256 validation follows
    // verifyHashes, while exact mode validation runs on Linux/macOS only.
    if (verifyModes && mode != metadata['mode']) {
      issues.add('BepInEx extracted entry mode drifted: $relative.');
    }
    if (verifyHashes &&
        await _sha256File(File(entity.path)) != metadata['sha256']) {
      issues.add('BepInEx extracted entry hash drifted: $relative.');
    }
  }
  if (!_sameSet(actualDirectories, expectedDirectories) ||
      !_sameSet(actualFiles, expectedFiles.keys.toSet())) {
    issues.add(
      'BepInEx extracted tree has missing or extra entries: ${extracted.path}.',
    );
  }
}

Map<String, Object?>? _findManifest(String root, String id) {
  for (final directory in Directory(
    p.join(root, 'mods'),
  ).listSync().whereType<Directory>()) {
    final file = File(p.join(directory.path, 'topiaforge.mod.json'));
    if (!file.existsSync()) {
      continue;
    }
    final json = _readObject(file);
    if ((json['name'] as String? ?? '').toLowerCase() == id.toLowerCase()) {
      return json;
    }
  }
  return null;
}

void _expectYamlVersion(
  String root,
  String path,
  String? expected,
  List<String> issues,
) {
  final text = readBoundedTextFileSync(
    File(p.join(root, path)),
    maxBytes: CliFileLimits.manifest,
  );
  final match = RegExp(
    r'''^version:\s*['"]?([^'"\s]+)''',
    multiLine: true,
  ).firstMatch(text);
  final actual = match?.group(1)?.split('+').first;
  if (actual != expected) {
    issues.add('$path version $actual does not match $expected.');
  }
}

void _expectJsonVersion(
  String root,
  String path,
  String? expected,
  List<String> issues,
) {
  final actual = _readObject(File(p.join(root, path)))['version'];
  if (actual != expected) {
    issues.add('$path version $actual does not match $expected.');
  }
}

void _expectCsprojVersion(
  String root,
  String path,
  String? expected,
  List<String> issues, {
  String? expectedAssemblyVersion,
}) {
  final text = readBoundedTextFileSync(
    File(p.join(root, path)),
    maxBytes: CliFileLimits.metadata,
  );
  for (final element in const [
    'Version',
    'AssemblyVersion',
    'FileVersion',
    'InformationalVersion',
  ]) {
    final match = RegExp('<$element>([^<]+)</$element>').firstMatch(text);
    final actual = match?.group(1)?.trim();
    final numericVersion = _numericAssemblyVersion(expected);
    final required = switch (element) {
      'AssemblyVersion' => expectedAssemblyVersion ?? numericVersion,
      'FileVersion' => numericVersion,
      _ => expected,
    };
    final matches = switch (element) {
      'AssemblyVersion' || 'FileVersion' => actual == required,
      _ => actual == expected,
    };
    if (!matches) {
      issues.add('$path $element $actual does not match $required.');
    }
  }
}

void _expectSdkCsprojVersion(
  String root,
  String path,
  String packageId,
  String? expected,
  List<String> issues,
) {
  _expectCsprojVersion(
    root,
    path,
    expected,
    issues,
    expectedAssemblyVersion: _v1StableContractPackageIds.contains(packageId)
        ? _stableMajorAssemblyVersion(expected)
        : null,
  );
  final text = readBoundedTextFileSync(
    File(p.join(root, path)),
    maxBytes: CliFileLimits.metadata,
  );
  final actual = RegExp(
    r'<PackageVersion>([^<]+)</PackageVersion>',
  ).firstMatch(text)?.group(1)?.trim();
  if (actual != expected) {
    issues.add('$path PackageVersion $actual does not match $expected.');
  }
}

const _v1StableContractPackageIds = <String>{
  'TopiaForge.Mods.Abstractions',
  'TopiaForge.Mods.Chronos',
  'TopiaForge.Mods.Multiplayer',
  'TopiaForge.Mods.Prompts',
  'TopiaForge.Mods.RobotKit',
  'TopiaForge.Mods.Testing',
  'TopiaForge.Mods.Ugc',
  'TopiaForge.Mods.Worlds',
};

String? _stableMajorAssemblyVersion(String? packageVersion) {
  final match = RegExp(r'^(\d+)\.').firstMatch(packageVersion ?? '');
  return match == null ? null : '${match.group(1)}.0.0.0';
}

String? _numericAssemblyVersion(String? packageVersion) {
  final match = RegExp(
    r'^(\d+)\.(\d+)\.(\d+)(?:[-+]|$)',
  ).firstMatch(packageVersion ?? '');
  return match == null
      ? null
      : '${match.group(1)}.${match.group(2)}.${match.group(3)}.0';
}

extension _ReleasePolicyModValidation on ReleasePolicyValidator {
  void _validateMods(
    TopiaForgeReleaseCatalogEntry release,
    String root,
    List<String> issues,
  ) {
    final found = <String, String>{};
    final modsRoot = Directory(p.join(root, 'mods'));
    for (final directory in listBoundedDirectorySync(
      modsRoot,
    ).whereType<Directory>()) {
      final file = File(p.join(directory.path, 'topiaforge.mod.json'));
      if (!file.existsSync()) continue;
      final json = _readObject(file);
      final id = json['name'] as String? ?? '';
      final version = json['version'] as String? ?? '';
      if (found.containsKey(id.toLowerCase())) {
        issues.add('Duplicate first-party mod id $id.');
      }
      found[id.toLowerCase()] = version;
      final projects = listBoundedDirectorySync(directory)
          .whereType<File>()
          .where((candidate) => p.extension(candidate.path) == '.csproj')
          .toList();
      if (projects.length != 1) {
        issues.add(
          '$id must have exactly one first-party mod project; found ${projects.length}.',
        );
      } else {
        _expectCsprojVersion(
          root,
          p.relative(projects.single.path, from: root),
          version,
          issues,
        );
      }
      final changelog = File(p.join(directory.path, 'CHANGELOG.md'));
      if (FileSystemEntity.typeSync(changelog.path, followLinks: false) !=
          FileSystemEntityType.file) {
        issues.add('$id is missing a regular CHANGELOG.md.');
      } else {
        final text = readBoundedTextFileSync(
          changelog,
          maxBytes: CliFileLimits.changelog,
        );
        if (!RegExp(
          '^## ${RegExp.escape(version)}(?:\\s|\$)',
          multiLine: true,
        ).hasMatch(text)) {
          issues.add('$id CHANGELOG.md has no release heading for $version.');
        }
      }
    }
    final expected = {...release.mods, ...release.excludedDeveloperMods};
    if (!_sameMap(expected, found)) {
      issues.add(
        'First-party mod ids/versions do not match release/catalog.json.',
      );
    }
  }
}

Map<String, Object?> _readObject(File file) {
  try {
    return readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.metadata);
  } on FileSystemException catch (error) {
    throw StateError('Could not read ${file.path}: ${error.message}');
  } on FormatException catch (error) {
    throw StateError('${file.path} is invalid JSON: ${error.message}');
  }
}

Map<String, Object?> _object(Object? value, String label) {
  if (value is! Map) throw StateError('$label must be a JSON object.');
  return value.map((key, value) => MapEntry(key.toString(), value));
}

Map<String, String> _stringMap(Object? value, String label) {
  final map = _object(value, label);
  if (map.values.any((item) => item is! String)) {
    throw StateError('$label must contain string values.');
  }
  return Map.unmodifiable(
    map.map((key, value) => MapEntry(key, value as String)),
  );
}

List<String> _stringList(Object? value, String label) {
  if (value is! List || value.any((item) => item is! String)) {
    throw StateError('$label must be an array of strings.');
  }
  return List.unmodifiable(value.cast<String>());
}

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);

bool _sameMap(Map<String, String> left, Map<String, String> right) =>
    left.length == right.length &&
    left.entries.every((entry) => right[entry.key] == entry.value);

bool _isSha256(Object? value) =>
    value is String && RegExp(r'^[0-9a-f]{64}$').hasMatch(value);

Future<String> _sha256File(File file) async =>
    (await sha256.bind(file.openRead()).single).toString();
