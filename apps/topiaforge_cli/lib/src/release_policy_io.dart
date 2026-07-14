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
    if (stat.size != metadata['size'] || mode != metadata['mode']) {
      issues.add('BepInEx extracted entry size/mode drifted: $relative.');
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
  List<String> issues,
) {
  final text = readBoundedTextFileSync(
    File(p.join(root, path)),
    maxBytes: CliFileLimits.metadata,
  );
  for (final element in const [
    'Version',
    'FileVersion',
    'InformationalVersion',
  ]) {
    final match = RegExp('<$element>([^<]+)</$element>').firstMatch(text);
    final actual = match?.group(1)?.trim();
    if (actual == null ||
        (element == 'FileVersion'
            ? !actual.startsWith('$expected')
            : actual != expected)) {
      issues.add('$path $element $actual does not match $expected.');
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
