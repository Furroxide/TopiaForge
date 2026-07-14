import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;

void main() {
  test('Xcode Run and Profile use the checkout payload root', () {
    final scheme = File(
      p.join(
        _launcherRoot().path,
        'macos',
        'Runner.xcodeproj',
        'xcshareddata',
        'xcschemes',
        'Runner.xcscheme',
      ),
    ).readAsStringSync();

    expect(
      RegExp(
        r'key = "TOPIAFORGE_REPOSITORY_ROOT"\s*'
        r'value = "\$\(PROJECT_DIR\)/\.\./\.\./\.\."\s*'
        r'isEnabled = "YES"',
      ).allMatches(scheme),
      hasLength(2),
    );
  });

  test('Xcode shell phases suppress environment logging', () {
    final project = File(
      p.join(
        _launcherRoot().path,
        'macos',
        'Runner.xcodeproj',
        'project.pbxproj',
      ),
    ).readAsStringSync();
    final section = project.substring(
      project.indexOf('/* Begin PBXShellScriptBuildPhase section */'),
      project.indexOf('/* End PBXShellScriptBuildPhase section */'),
    );

    final phaseCount = RegExp(
      r'isa = PBXShellScriptBuildPhase;',
    ).allMatches(section).length;
    final suppressedCount = RegExp(
      r'showEnvVarsInLog = 0;',
    ).allMatches(section).length;

    expect(phaseCount, greaterThan(0));
    expect(suppressedCount, phaseCount);
  });

  test('native runners use canonical TopiaForge identities', () {
    final root = _launcherRoot().path;
    final linux = File(
      p.join(root, 'linux', 'CMakeLists.txt'),
    ).readAsStringSync();
    final windows = File(
      p.join(root, 'windows', 'CMakeLists.txt'),
    ).readAsStringSync();
    final windowsMetadata = File(
      p.join(root, 'windows', 'runner', 'Runner.rc'),
    ).readAsStringSync();
    final macos = File(
      p.join(root, 'macos', 'Runner', 'Configs', 'AppInfo.xcconfig'),
    ).readAsStringSync();
    final macosProject = File(
      p.join(root, 'macos', 'Runner.xcodeproj', 'project.pbxproj'),
    ).readAsStringSync();
    final snap = File(
      p.join(root, 'snap', 'snapcraft.yaml'),
    ).readAsStringSync();
    final desktop = File(
      p.join(
        root,
        'snap',
        'gui',
        'io.github.furroxide.topiaforge.launcher.desktop',
      ),
    ).readAsStringSync();

    expect(linux, contains('set(BINARY_NAME "topiaforge_launcher")'));
    expect(
      linux,
      contains('set(APPLICATION_ID "io.github.furroxide.topiaforge.launcher")'),
    );
    expect(windows, contains('project(topiaforge_launcher LANGUAGES CXX)'));
    expect(windows, contains('set(BINARY_NAME "topiaforge_launcher")'));
    expect(windowsMetadata, contains('"topiaforge_launcher.exe"'));
    expect(macos, contains('PRODUCT_NAME = TopiaForge'));
    expect(macos, contains('EXECUTABLE_NAME = topiaforge_launcher'));
    expect(
      macos,
      contains(
        'PRODUCT_BUNDLE_IDENTIFIER = io.github.furroxide.topiaforge.launcher',
      ),
    );
    expect(
      RegExp(
        r'TEST_HOST = ".*TopiaForge\.app.*\/topiaforge_launcher";',
      ).allMatches(macosProject),
      hasLength(3),
    );
    expect(snap, contains('name: topiaforge'));
    expect(snap, contains('command: topiaforge_launcher'));
    expect(
      snap,
      contains('common-id: io.github.furroxide.topiaforge.launcher'),
    );
    expect(
      snap,
      contains(
        'desktop-file-ids:\n      - io.github.furroxide.topiaforge.launcher',
      ),
    );
    expect(snap, contains('name: io.github.furroxide.topiaforge.launcher'));
    expect(desktop, contains('Exec=topiaforge'));
    expect(desktop, contains('Icon=\${SNAP}/meta/gui/topiaforge.png'));
  });
}

Directory _launcherRoot() {
  var current = Directory.current.absolute;
  while (true) {
    final pubspec = File(p.join(current.path, 'pubspec.yaml'));
    if (pubspec.existsSync() &&
        pubspec.readAsStringSync().contains(
          'name: topiaforge_launcher_flutter',
        )) {
      return current;
    }
    final nested = Directory(
      p.join(current.path, 'apps', 'topiaforge_launcher_flutter'),
    );
    if (File(p.join(nested.path, 'pubspec.yaml')).existsSync()) {
      return nested;
    }
    if (current.parent.path == current.path) {
      throw StateError('Could not locate the launcher project.');
    }
    current = current.parent;
  }
}
