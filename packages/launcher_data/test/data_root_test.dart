import 'package:launcher_data/src/data_root.dart';
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
}
