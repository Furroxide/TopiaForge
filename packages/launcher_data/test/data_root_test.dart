import 'package:launcher_data/src/data_root.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('explicit Robotopia data root overrides platform defaults', () {
    expect(
      resolveRobotopiaDataRoot(
        environment: const {
          'ROBOTOPIA_DATA_ROOT': '  /portable/robotopia-data  ',
          'APPDATA': r'C:\Users\test\AppData\Roaming',
          'HOME': '/home/test',
        },
        isWindows: true,
      ),
      '/portable/robotopia-data',
    );
  });

  test('launcher and developer defaults share the Windows data root', () {
    expect(
      resolveRobotopiaDataRoot(
        environment: const {'APPDATA': r'C:\Users\test\AppData\Roaming'},
        isWindows: true,
      ),
      p.join(r'C:\Users\test\AppData\Roaming', 'RobotopiaLauncher'),
    );
  });

  test('falls back to the current directory without a home directory', () {
    expect(
      resolveRobotopiaDataRoot(
        environment: const {},
        isWindows: false,
        currentDirectory: '/workspace',
      ),
      p.join('/workspace', '.robotopia_launcher'),
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
