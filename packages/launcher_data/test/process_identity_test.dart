import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/process_identity.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('argument matching requires the exact absolute executable path', () {
    expect(
      processArgumentsReferenceExecutable([
        '/usr/bin/wine',
        '/games/a/Robotopia.exe',
      ], '/games/a/Robotopia.exe'),
      isTrue,
    );
    expect(
      processArgumentsReferenceExecutable([
        '/usr/bin/wine',
        '/games/b/Robotopia.exe',
      ], '/games/a/Robotopia.exe'),
      isFalse,
      reason: 'a second Robotopia install must never be terminated',
    );
    expect(
      processArgumentsReferenceExecutable([
        '/usr/bin/wine',
        'Robotopia.exe',
      ], '/games/a/Robotopia.exe'),
      isFalse,
      reason: 'basename-only matching is intentionally unsafe',
    );
  });

  test(
    'Linux proc enumeration ignores unrelated and malformed entries',
    () async {
      final root = await Directory.systemTemp.createTemp(
        'topiaforge-proc-test-',
      );
      addTearDown(() async {
        if (await root.exists()) {
          await root.delete(recursive: true);
        }
      });
      await _writeCommandLine(root, 101, [
        '/usr/bin/wine',
        '/games/selected/Robotopia.exe',
      ]);
      await _writeCommandLine(root, 202, [
        '/usr/bin/wine',
        '/games/other/Robotopia.exe',
      ]);
      Directory(p.join(root.path, 'not-a-pid')).createSync();

      final matches = await findLinuxGameProcessIds(
        '/games/selected/Robotopia.exe',
        procRoot: root,
      );

      expect(matches, [101]);
    },
  );

  test('POSIX regex escaping keeps executable paths literal', () {
    expect(
      escapePosixExtendedRegex('/Games/Robotopia (Test)+/Robotopia.app'),
      r'/Games/Robotopia \(Test\)\+/Robotopia\.app',
    );
  });
}

Future<void> _writeCommandLine(
  Directory procRoot,
  int processId,
  List<String> arguments,
) async {
  final directory = Directory(p.join(procRoot.path, '$processId'));
  await directory.create(recursive: true);
  final bytes = <int>[];
  for (final argument in arguments) {
    bytes
      ..addAll(utf8.encode(argument))
      ..add(0);
  }
  await File(p.join(directory.path, 'cmdline')).writeAsBytes(bytes);
}
