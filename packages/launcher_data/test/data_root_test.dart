import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('explicit TopiaForge data root overrides platform defaults', () {
    expect(
      resolveTopiaForgeDataRoot(
        environment: const {
          'TOPIAFORGE_DATA_ROOT': '  /portable/topiaforge-data  ',
          'APPDATA': r'C:\Users\test\AppData\Roaming',
          'HOME': '/home/test',
        },
        isWindows: true,
      ),
      '/portable/topiaforge-data',
    );
  });

  test('launcher and developer defaults share the Windows data root', () {
    expect(
      resolveTopiaForgeDataRoot(
        environment: const {'APPDATA': r'C:\Users\test\AppData\Roaming'},
        isWindows: true,
      ),
      p.join(r'C:\Users\test\AppData\Roaming', 'TopiaForgeLauncher'),
    );
  });

  test('retired environment variables and data roots are ignored', () {
    expect(
      resolveTopiaForgeDataRoot(
        environment: const {
          'ROBOTO'
                  'PIA_DATA_ROOT':
              '/retired/data',
          'HOME': '/home/test',
        },
        isWindows: false,
      ),
      p.join('/home/test', '.topiaforge_launcher'),
    );
  });

  test('falls back to the current directory without a home directory', () {
    expect(
      resolveTopiaForgeDataRoot(
        environment: const {},
        isWindows: false,
        currentDirectory: '/workspace',
      ),
      p.join('/workspace', '.topiaforge_launcher'),
    );
  });

  test('explicit constructor roots override shared defaults', () {
    final launcher = LocalLauncherRepository(
      dataRoot: '/explicit/launcher',
      repositoryRoot: '/tmp',
    );
    final developer = LocalDeveloperRepository(
      dataRoot: '/explicit/developer',
      repositoryRoot: '/tmp',
    );

    expect(launcher.dataRoot, '/explicit/launcher');
    expect(developer.developerDataRoot, '/explicit/developer');
  });

  group('the default data root', () {
    const windows = {r'APPDATA': r'C:\Users\player\AppData\Roaming'};
    const posix = {'HOME': '/home/player'};

    bool onWindows(String root, [Map<String, String> extra = const {}]) =>
        isDefaultTopiaForgeDataRoot(
          root,
          environment: {...windows, ...extra},
          isWindows: true,
          currentDirectory: r'C:\Users\player',
        );

    bool onPosix(String root, [Map<String, String> extra = const {}]) =>
        isDefaultTopiaForgeDataRoot(
          root,
          environment: {...posix, ...extra},
          isWindows: false,
          currentDirectory: '/home/player',
        );

    // resolveTopiaForgeDataRoot joins with the host's separator, so POSIX
    // roots are only meaningful on a POSIX host.
    final posixHost = Platform.isWindows
        ? 'POSIX roots need a POSIX host'
        : null;

    test('is the root a user gets without TOPIAFORGE_DATA_ROOT', () {
      expect(
        onWindows(r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher'),
        isTrue,
      );
    });

    test('ignores spelling and case on Windows', () {
      expect(
        onWindows(r'c:/users/PLAYER/appdata/roaming/topiaforgelauncher/'),
        isTrue,
      );
      expect(onWindows(r'AppData\Roaming\.\x\..\TopiaForgeLauncher'), isTrue);
    });

    test('ignores spelling but not case on POSIX', () {
      expect(onPosix('/home/player/.topiaforge_launcher'), isTrue);
      expect(onPosix('/home/player/x/../.topiaforge_launcher/'), isTrue);
      expect(onPosix('.topiaforge_launcher'), isTrue);
      expect(onPosix('/home/player/.TopiaForge_Launcher'), isFalse);
      expect(onPosix('/tmp/acceptance/launcher'), isFalse);
    }, skip: posixHost);

    test('stays the default when TOPIAFORGE_DATA_ROOT names it', () {
      final named = {
        'TOPIAFORGE_DATA_ROOT':
            r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher',
      };
      expect(
        onWindows(r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher', named),
        isTrue,
      );
    });

    test('is never another root', () {
      for (final root in [
        r'D:\Portable\TopiaForge\data',
        r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher2',
        r'C:\Temp\release-template-data',
      ]) {
        expect(onWindows(root), isFalse, reason: root);
      }
    });

    test('is not whatever TOPIAFORGE_DATA_ROOT names', () {
      // A launcher started on a scratch root sees that root in its own
      // environment; it must still not count as the default one.
      const scratch = r'C:\Temp\acceptance\launcher';
      expect(onWindows(scratch, {'TOPIAFORGE_DATA_ROOT': scratch}), isFalse);
    });
  });
}
