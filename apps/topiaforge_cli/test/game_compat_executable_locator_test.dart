import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/game_compat_executable_locator.dart';

void main() {
  const locator = GameCompatExecutableLocator();
  late Directory root;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-game-compat-');
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('finds a regular packaged extractor beside the compiled CLI', () {
    final cli = File(p.join(root.path, 'topiaforge'))..writeAsStringSync('cli');
    final extractor = File(p.join(root.path, 'TopiaForge.GameCompat.Extractor'))
      ..writeAsStringSync('extractor');

    expect(locator.findPackaged(resolvedExecutable: cli.path), extractor.path);
  });

  test('supports the Windows packaged executable names', () {
    final cli = File(p.join(root.path, 'topiaforge.exe'))
      ..writeAsStringSync('cli');
    final extractor = File(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor.exe'),
    )..writeAsStringSync('extractor');

    expect(
      locator.findPackaged(resolvedExecutable: cli.path, isWindows: true),
      extractor.path,
    );
  });

  test('supports both macOS architecture-specific CLI names', () {
    final extractor = File(p.join(root.path, 'TopiaForge.GameCompat.Extractor'))
      ..writeAsStringSync('extractor');

    for (final name in const ['topiaforge-arm64', 'topiaforge-x64']) {
      final cli = File(p.join(root.path, name))..writeAsStringSync('cli');
      expect(
        locator.findPackaged(resolvedExecutable: cli.path, isWindows: false),
        extractor.path,
      );
    }
  });

  test('does not infer a release root from a Dart host executable', () {
    final dart = File(p.join(root.path, 'dart'))..writeAsStringSync('host');
    File(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor'),
    ).writeAsStringSync('extractor');

    expect(locator.findPackaged(resolvedExecutable: dart.path), isNull);
  });

  test('rejects lookalike CLI names', () {
    final cli = File(p.join(root.path, 'topiaforge-backup'))
      ..writeAsStringSync('cli');
    File(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor'),
    ).writeAsStringSync('extractor');

    expect(
      locator.findPackaged(resolvedExecutable: cli.path, isWindows: false),
      isNull,
    );
  });

  test('does not allow a Windows extractor to shadow the Unix sibling', () {
    final cli = File(p.join(root.path, 'topiaforge'))..writeAsStringSync('cli');
    File(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor.exe'),
    ).writeAsStringSync('extractor');

    expect(
      locator.findPackaged(resolvedExecutable: cli.path, isWindows: false),
      isNull,
    );
  });

  test('rejects a linked CLI trust root', () {
    if (Platform.isWindows) {
      return;
    }
    final target = File(p.join(root.path, 'target-cli'))
      ..writeAsStringSync('cli');
    final cli = Link(p.join(root.path, 'topiaforge'))..createSync(target.path);
    File(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor'),
    ).writeAsStringSync('extractor');

    expect(locator.findPackaged(resolvedExecutable: cli.path), isNull);
  });

  test('rejects a linked extractor', () {
    if (Platform.isWindows) {
      return;
    }
    final cli = File(p.join(root.path, 'topiaforge'))..writeAsStringSync('cli');
    final target = File(p.join(root.path, 'target'))
      ..writeAsStringSync('extractor');
    Link(
      p.join(root.path, 'TopiaForge.GameCompat.Extractor'),
    ).createSync(target.path);

    expect(locator.findPackaged(resolvedExecutable: cli.path), isNull);
  });
}
