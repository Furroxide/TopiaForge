import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory tempDir;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('game_layout_test');
  });

  tearDown(() {
    tempDir.deleteSync(recursive: true);
  });

  void createWindowsGame(Directory root) {
    File(p.join(root.path, 'Robotopia.exe')).createSync(recursive: true);
  }

  void createMacGame(Directory root) {
    File(
      p.join(root.path, 'Robotopia.app', 'Contents', 'MacOS', 'robotopia'),
    ).createSync(recursive: true);
  }

  group('GameLayout.resolve', () {
    test('windows exe on a windows host resolves as windowsNative', () {
      createWindowsGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'windows');
      expect(layout, isNotNull);
      expect(layout!.kind, GameInstallLayout.windowsNative);
      expect(layout.executablePath, p.join(tempDir.path, 'Robotopia.exe'));
      expect(
        layout.managedDirPath,
        p.join(tempDir.path, 'Robotopia_Data', 'Managed'),
      );
    });

    test('windows exe on a linux host resolves as linuxProton', () {
      createWindowsGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'linux');
      expect(layout!.kind, GameInstallLayout.linuxProton);
      expect(layout.executablePath, p.join(tempDir.path, 'Robotopia.exe'));
    });

    test('windows exe on a macos host resolves as linuxProton (Wine)', () {
      createWindowsGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'macos');
      expect(layout!.kind, GameInstallLayout.linuxProton);
    });

    test('app bundle resolves as macAppBundle', () {
      createMacGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'macos');
      expect(layout!.kind, GameInstallLayout.macAppBundle);
      expect(layout.gameRoot, Directory(tempDir.path).absolute.path);
      expect(
        layout.executablePath,
        p.join(
          tempDir.path,
          'Robotopia.app',
          'Contents',
          'MacOS',
          'robotopia',
        ),
      );
      expect(
        layout.managedDirPath,
        p.join(
          tempDir.path,
          'Robotopia.app',
          'Contents',
          'Resources',
          'Data',
          'Managed',
        ),
      );
    });

    test('selecting the .app bundle itself normalizes to its parent', () {
      createMacGame(tempDir);
      final layout = GameLayout.resolve(
        p.join(tempDir.path, 'Robotopia.app'),
        hostPlatform: 'macos',
      );
      expect(layout!.kind, GameInstallLayout.macAppBundle);
      expect(layout.gameRoot, Directory(tempDir.path).absolute.path);
    });

    test('empty directory resolves as null', () {
      expect(GameLayout.resolve(tempDir.path, hostPlatform: 'windows'), isNull);
      expect(GameLayout.resolve(tempDir.path, hostPlatform: 'linux'), isNull);
    });
  });

  group('layout properties', () {
    test('windows layout uses the win_x64 bundle and doorstop markers', () {
      createWindowsGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'windows')!;
      expect(layout.bepInExBundleDirName, startsWith('win_x64_'));
      expect(layout.bepInExMarkerFiles, contains('winhttp.dll'));
      expect(layout.launchEnvironment(), isEmpty);
      expect(layout.executableRuntimeFiles, isEmpty);
    });

    test('proton layout uses the win_x64 bundle and WINEDLLOVERRIDES', () {
      createWindowsGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'linux')!;
      expect(layout.bepInExBundleDirName, startsWith('win_x64_'));
      expect(
        layout.launchEnvironment(),
        containsPair('WINEDLLOVERRIDES', 'winhttp=n,b'),
      );
    });

    test('mac layout uses the universal bundle and DYLD doorstop env', () {
      createMacGame(tempDir);
      final layout = GameLayout.resolve(tempDir.path, hostPlatform: 'macos')!;
      expect(layout.bepInExBundleDirName, startsWith('macos_universal_'));
      expect(layout.bepInExMarkerFiles, contains('run_bepinex.sh'));
      expect(layout.executableRuntimeFiles, contains('run_bepinex.sh'));

      final env = layout.launchEnvironment();
      expect(env['DOORSTOP_ENABLED'], '1');
      expect(
        env['DOORSTOP_TARGET_ASSEMBLY'],
        p.join(layout.gameRoot, 'BepInEx', 'core', 'BepInEx.Preloader.dll'),
      );
      expect(
        env['DYLD_INSERT_LIBRARIES'],
        p.join(layout.gameRoot, 'libdoorstop.dylib'),
      );
      expect(env['DYLD_LIBRARY_PATH'], layout.gameRoot);
    });
  });
}
