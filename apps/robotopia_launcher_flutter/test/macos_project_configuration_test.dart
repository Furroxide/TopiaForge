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
        r'key = "ROBOTOPIA_REPOSITORY_ROOT"\s*'
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
}

Directory _launcherRoot() {
  var current = Directory.current.absolute;
  while (true) {
    final pubspec = File(p.join(current.path, 'pubspec.yaml'));
    if (pubspec.existsSync() &&
        pubspec.readAsStringSync().contains(
          'name: robotopia_launcher_flutter',
        )) {
      return current;
    }
    final nested = Directory(
      p.join(current.path, 'apps', 'robotopia_launcher_flutter'),
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
