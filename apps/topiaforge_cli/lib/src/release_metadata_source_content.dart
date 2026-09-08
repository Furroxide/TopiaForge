import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_git_contract.dart';

/// Index hints are not evidence that a consumed working file matches its blob.
Future<void> verifyMetadataSourceContents(
  String root,
  String targetSha,
  List<String> inputPaths,
) async {
  final paths = inputPaths.map((path) => path.replaceAll('\\', '/')).toList();
  final attributes = await _frozenAttributes(root, targetSha, paths);
  for (final path in paths) {
    final attrs = attributes[path]!;
    final filter = attrs['filter'];
    if (!['unspecified', 'unset', 'lfs'].contains(filter)) {
      throw StateError('Unsupported publication source filter: $path.');
    }
    final frozen = await readReleaseGitBlob(
      root,
      targetSha,
      path,
      CliFileLimits.metadata,
    );
    final actual = readBoundedRegularFileSync(
      File(p.join(root, path)),
      maxBytes: filter == 'lfs'
          ? CliFileLimits.uiBundle
          : CliFileLimits.metadata,
    );
    if (filter == 'lfs') {
      _verifyLfsContent(path, frozen, actual);
      continue;
    }
    // Reproduce only Git's text newline normalization, never execute configured
    // clean/smudge programs. Frozen -text declarations keep byte comparison.
    final normalize =
        attrs['text'] != 'unset' &&
        (attrs['text'] == 'set' ||
            (attrs['text'] == 'auto' &&
                !frozen.contains(0) &&
                !actual.contains(0)) ||
            ['lf', 'crlf'].contains(attrs['eol']));
    final expectedBytes = normalize ? _toLf(frozen) : frozen;
    final actualBytes = normalize ? _toLf(actual) : actual;
    if (expectedBytes.length != actualBytes.length ||
        sha256.convert(expectedBytes) != sha256.convert(actualBytes)) {
      _different(path);
    }
  }
}

Future<Map<String, Map<String, String>>> _frozenAttributes(
  String root,
  String targetSha,
  List<String> paths,
) async {
  final result = await Process.run('git', [
    '--no-replace-objects',
    '-C',
    root,
    'check-attr',
    '--source=$targetSha',
    '-z',
    'text',
    'eol',
    'filter',
    '--',
    ...paths,
  ]);
  if (result.exitCode != 0 ||
      result.stdout is! String ||
      (result.stdout as String).length > 1024 * 1024) {
    throw StateError('Cannot read frozen publication source attributes.');
  }
  final fields = (result.stdout as String).split('\x00');
  if (fields.removeLast().isNotEmpty || fields.length != paths.length * 9) {
    throw StateError('Invalid frozen publication source attributes.');
  }
  final attributes = <String, Map<String, String>>{
    for (final path in paths) path: <String, String>{},
  };
  for (var index = 0; index < fields.length; index += 3) {
    final attrs = attributes[fields[index]];
    final name = fields[index + 1];
    if (attrs == null ||
        !['text', 'eol', 'filter'].contains(name) ||
        attrs.containsKey(name)) {
      throw StateError('Invalid frozen publication source attribute identity.');
    }
    attrs[name] = fields[index + 2];
  }
  if (attributes.values.any((attrs) => attrs.length != 3)) {
    throw StateError('Incomplete frozen publication source attributes.');
  }
  return attributes;
}

void _verifyLfsContent(String path, List<int> frozen, List<int> actual) {
  final pointer = RegExp(
    r'^version https://git-lfs.github.com/spec/v1\n'
    r'oid sha256:([0-9a-f]{64})\nsize ([1-9][0-9]*)\n$',
  ).firstMatch(utf8.decode(frozen, allowMalformed: false));
  if (pointer == null ||
      int.tryParse(pointer.group(2)!) != actual.length ||
      pointer.group(1) != sha256.convert(actual).toString()) {
    _different(path);
  }
}

List<int> _toLf(List<int> bytes) => [
  for (var index = 0; index < bytes.length; index += 1)
    if (bytes[index] != 13 ||
        index + 1 == bytes.length ||
        bytes[index + 1] != 10)
      bytes[index],
];

Never _different(String path) => throw StateError(
  'Publication metadata source differs from the exact target SHA: $path.',
);
