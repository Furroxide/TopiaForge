import 'dart:io';

import 'package:test/test.dart';

void main() {
  test('prints help for the public robotopia executable', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      'run',
      'bin/robotopia.dart',
      'help',
    ], workingDirectory: Directory.current.path);

    expect(result.exitCode, 0);
    expect(result.stdout.toString(), contains('robotopia new mod'));
    expect(result.stdout.toString(), contains('robotopia restore'));
  });

  test('lists built-in templates', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      'run',
      'bin/robotopia.dart',
      'list',
      'templates',
    ], workingDirectory: Directory.current.path);

    expect(result.exitCode, 0);
    expect(result.stdout.toString(), contains('mod'));
    expect(result.stdout.toString(), contains('asset-companion'));
  });
}
