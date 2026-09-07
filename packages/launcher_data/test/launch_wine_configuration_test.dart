import 'dart:io';

import 'package:launcher_data/src/launch_wine_configuration.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-wine-setting-');
  });
  tearDown(() {
    root.deleteSync(recursive: true);
  });

  test(
    'relative and malformed Wine settings require an explicit full path',
    () {
      for (final value in ['wine', './wine', null, '', 42, []]) {
        expect(
          () => configuredWineExecutable(value),
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'repair guidance',
              contains('full path'),
            ),
          ),
          reason: '$value',
        );
      }
    },
  );

  test(
    'missing and nonordinary Wine executables are refused before launch',
    () {
      for (final path in [p.join(root.path, 'missing-wine'), root.path]) {
        expect(
          () => configuredWineExecutable(path),
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'repair guidance',
              contains('regular file'),
            ),
          ),
        );
      }
    },
  );

  test('explicit existing Wine executable keeps its canonical identity', () {
    final file = File(p.join(root.path, 'test wine'))
      ..writeAsStringSync('fixture is never executed');
    expect(
      configuredWineExecutable(file.path),
      file.resolveSymbolicLinksSync(),
    );
  });
}
