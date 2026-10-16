import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

const _plainId = 'io.github.furroxide.topiaforge.launcher';

/// The Linux runner picks its D-Bus application id before Dart runs, so it
/// decides by itself whether a launcher is on the default data root. This
/// compiles that code (`linux/runner/data_root_id.cc`) and holds it to
/// Dart's [isDefaultTopiaForgeDataRoot] wherever the two could part: a root
/// that only Dart called the default would register the desktop entry but
/// run under a private D-Bus name, so every link would start a second
/// launcher on the same root.
void main() {
  final skip = _skipReason();
  late _Runner runner;

  setUpAll(() {
    if (skip == null) runner = _Runner.compile();
  });

  test('keeps the plain id exactly where Dart sees the default data root', () {
    for (final start in _starts()) {
      expect(
        runner.idFor(start) == _plainId,
        start.isDefaultToDart,
        reason: '$start',
      );
    }
  }, skip: skip);

  test('trims exactly the whitespace Dart trims', () {
    for (final unit in _dartWhitespace()) {
      final space = String.fromCharCode(unit);
      final start = _Start(
        configured: '$space/home/u/.topiaforge_launcher$space',
        home: '/home/u',
      );
      expect(start.isDefaultToDart, isTrue, reason: '$start');
      expect(runner.idFor(start), _plainId, reason: '$start');
    }
    for (final unit in [0x180E, 0x200B, 0x2060, 0xFFFE, 0x00AD]) {
      final start = _Start(
        configured: '${String.fromCharCode(unit)}/home/u/.topiaforge_launcher',
        home: '/home/u',
      );
      expect(start.isDefaultToDart, isFalse, reason: '$start');
      expect(runner.idFor(start), isNot(_plainId), reason: '$start');
    }
  }, skip: skip);

  test('gives one root one id however it is spelled', () {
    final ids = {
      for (final spelling in [
        '/tmp/acceptance/launcher',
        '//tmp/acceptance/launcher/',
        ' /tmp/./acceptance/x/../launcher　',
        '../../tmp/acceptance/launcher',
      ])
        runner.idFor(_Start(configured: spelling, home: '/home/u')),
    };
    expect(ids, hasLength(1));
    expect(
      ids.single,
      matches(
        RegExp(r'^io\.github\.furroxide\.topiaforge\.launcher\.r[0-9a-f]{16}$'),
      ),
    );
    expect(
      runner.idFor(
        _Start(configured: '/tmp/acceptance/launcher2', home: '/home/u'),
      ),
      isNot(ids.single),
    );
  }, skip: skip);
}

/// The spellings: every one the review found, and the edges of each rule.
List<_Start> _starts() => [
  // The default data root, spelled many ways.
  _Start(home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '//home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '///home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher/', home: '/home/u'),
  _Start(configured: '/home/u//x/../.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '/home/u/./.topiaforge_launcher/.', home: '/home/u'),
  _Start(configured: '/../home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: '//home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: '/home/u/'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: '/home/./u'),
  _Start(configured: ' /home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: ' \t/home/u/.topiaforge_launcher\n', home: '/home/u'),
  _Start(configured: '﻿/home/u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '../u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: 'x/../.topiaforge_launcher', home: '/home/u'),
  _Start(
    configured: '.topiaforge_launcher',
    home: '/elsewhere',
    currentDirectory: '/elsewhere',
  ),
  _Start(configured: '/home/u/.topiaforge_launcher', userProfile: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: ''),
  _Start(configured: '.topiaforge_launcher', home: ''),
  _Start(configured: '', home: '/home/u'),
  _Start(configured: '  ', home: '/home/u'),
  _Start(configured: '　', home: '/home/u'),
  // Other roots.
  _Start(configured: '/home/u/.TopiaForge_Launcher', home: '/home/u'),
  _Start(configured: '/tmp/acceptance/launcher', home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher/..', home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher​', home: '/home/u'),
  _Start(configured: 'u/.topiaforge_launcher', home: '/home/u'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: '/home/v'),
  _Start(configured: '/home/u/.topiaforge_launcher', home: ' /home/u'),
  _Start(
    configured: '/home/u/.topiaforge_launcher',
    userProfile: '/home/u',
    home: '/home/v',
  ),
];

/// Every code point Dart's `String.trim()` removes.
List<int> _dartWhitespace() => [
  for (var unit = 0; unit <= 0x10FFFF; unit++)
    if ((unit < 0xD800 || unit > 0xDFFF) &&
        String.fromCharCode(unit).trim().isEmpty)
      unit,
];

/// One launcher start: its variables, each null when unset, and the
/// directory it starts in.
final class _Start {
  _Start({
    this.configured,
    this.home,
    this.userProfile,
    this.currentDirectory = '/home/u',
  });

  final String? configured;
  final String? home;
  final String? userProfile;
  final String currentDirectory;

  Map<String, String> get environment => {
    'TOPIAFORGE_DATA_ROOT': ?configured,
    'HOME': ?home,
    'USERPROFILE': ?userProfile,
  };

  /// What the launcher's Dart side decides for this start.
  bool get isDefaultToDart => isDefaultTopiaForgeDataRoot(
    resolveTopiaForgeDataRoot(
      environment: environment,
      isWindows: false,
      currentDirectory: currentDirectory,
    ),
    environment: environment,
    isWindows: false,
    currentDirectory: currentDirectory,
  );

  List<String> get arguments => [
    for (final value in [configured, home, userProfile, currentDirectory])
      value == null ? '-' : '=$value',
  ];

  @override
  String toString() =>
      'TOPIAFORGE_DATA_ROOT=${_show(configured)} HOME=${_show(home)} '
      'USERPROFILE=${_show(userProfile)} cwd=$currentDirectory';

  /// [value] quoted, with anything but printable ASCII as `\u{...}`.
  static String _show(String? value) {
    if (value == null) return '(unset)';
    final shown = StringBuffer('"');
    for (final rune in value.runes) {
      shown.write(
        rune > 0x20 && rune < 0x7F
            ? String.fromCharCode(rune)
            : '\\u{${rune.toRadixString(16)}}',
      );
    }
    return '$shown"';
  }
}

/// The runner's id code, compiled with `test/linux_application_id_harness.cc`.
final class _Runner {
  _Runner._(this._binary);

  factory _Runner.compile() {
    final root = _launcherRoot();
    final runner = p.join(root, 'linux', 'runner');
    final output = Directory.systemTemp.createTempSync('topiaforge-app-id-');
    addTearDown(() => output.deleteSync(recursive: true));
    final glib = Process.runSync('pkg-config', [
      '--cflags',
      '--libs',
      'glib-2.0',
    ]);
    final binary = p.join(output.path, 'harness');
    final compiled = Process.runSync('c++', [
      '-std=c++14',
      '-Wall',
      '-Werror',
      '-I',
      runner,
      '-o',
      binary,
      p.join(root, 'test', 'linux_application_id_harness.cc'),
      p.join(runner, 'data_root_id.cc'),
      ...(glib.stdout as String).trim().split(RegExp(r'\s+')),
    ]);
    if (compiled.exitCode != 0) {
      fail('The runner id code did not compile:\n${compiled.stderr}');
    }
    return _Runner._(binary);
  }

  final String _binary;

  String idFor(_Start start) {
    final result = Process.runSync(_binary, start.arguments);
    expect(result.exitCode, 0, reason: '$start: ${result.stderr}');
    return (result.stdout as String).trim();
  }
}

String? _skipReason() {
  if (!Platform.isLinux) return 'The Linux runner builds on Linux only.';
  try {
    final compiler = Process.runSync('c++', ['--version']);
    final glib = Process.runSync('pkg-config', ['--exists', 'glib-2.0']);
    if (compiler.exitCode != 0 || glib.exitCode != 0) {
      return 'Needs a C++ compiler and the GLib headers.';
    }
  } on ProcessException {
    return 'Needs a C++ compiler and the GLib headers.';
  }
  return null;
}

String _launcherRoot() {
  var current = Directory.current.absolute;
  while (true) {
    for (final candidate in [
      current.path,
      p.join(current.path, 'apps', 'topiaforge_launcher_flutter'),
    ]) {
      if (File(
        p.join(candidate, 'linux', 'runner', 'data_root_id.cc'),
      ).existsSync()) {
        return candidate;
      }
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      throw StateError('Could not locate the launcher project.');
    }
    current = parent;
  }
}
