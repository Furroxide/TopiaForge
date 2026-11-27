import 'dart:convert';
import 'dart:io';

/// Check the immutable blob size before buffering its content.
Future<List<int>> readReleaseGitBlob(
  String root,
  String sha,
  String path,
  int limit,
) async {
  final sizeResult = await Process.run(
    'git',
    ['--no-replace-objects', '-C', root, 'cat-file', '-s', '$sha:$path'],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  final size = int.tryParse((sizeResult.stdout as String).trim());
  if (sizeResult.exitCode != 0 || size == null || size <= 0 || size > limit) {
    throw StateError(
      'Required release contract is not tracked at the target commit, or has invalid size: $path.',
    );
  }
  final result = await Process.run(
    'git',
    ['--no-replace-objects', '-C', root, 'cat-file', 'blob', '$sha:$path'],
    stdoutEncoding: null,
    stderrEncoding: utf8,
  );
  if (result.exitCode != 0 ||
      result.stdout is! List<int> ||
      (result.stdout as List<int>).length != size) {
    throw StateError('Cannot read exact tracked release contract: $path.');
  }
  return List<int>.unmodifiable(result.stdout as List<int>);
}
