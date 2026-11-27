import 'dart:io';

import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_log_reader.dart';

void main() {
  late Directory root;
  late File log;
  setUp(() {
    root = Directory.systemTemp.createTempSync('acceptance-log-');
    log = File('${root.path}/manager.log');
  });
  tearDown(() => root.deleteSync(recursive: true));

  test(
    'each poll consumes a bounded chunk and eventually preserves every line',
    () async {
      final reader = AcceptanceIncrementalLogReader(log);
      final lines = List.generate(20000, (index) => 'bounded marker $index ✓');
      log.writeAsStringSync('${lines.join('\n')}\n');
      final first = await reader.readNewLines();
      expect(first.length, lessThan(lines.length));
      final all = [...first];
      while (all.length < lines.length) {
        final next = await reader.readNewLines();
        expect(next, isNotEmpty);
        all.addAll(next);
      }
      expect(all, lines);
    },
  );

  for (final terminated in [false, true]) {
    test(
      'oversized ${terminated ? 'complete' : 'partial'} lines fail closed',
      () async {
        final reader = AcceptanceIncrementalLogReader(log);
        log.writeAsStringSync('${'x' * 70000}${terminated ? '\n' : ''}');
        await expectLater(
          reader.readNewLines().then((_) {}),
          throwsFormatException,
        );
      },
    );
  }

  test('a partial line resumes and truncation starts a new record', () async {
    final reader = AcceptanceIncrementalLogReader(log);
    log.writeAsStringSync('first\r\nsec');
    expect(await reader.readNewLines(), ['first']);
    log.writeAsStringSync('ond\n', mode: FileMode.append);
    expect(await reader.readNewLines(), ['second']);
    log.writeAsStringSync('new\n');
    expect(await reader.readNewLines(), ['new']);
  });
}
