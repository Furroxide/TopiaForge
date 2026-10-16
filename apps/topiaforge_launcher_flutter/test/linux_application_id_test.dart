import 'dart:convert';
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

  test('counts a variable Dart cannot decode as unset, as Dart does', () {
    for (final (bytes, decoded) in _decodings()) {
      for (final start in [
        // As the data root, beside the HOME of the default root.
        _Start(
          configured: decoded,
          home: '/home/u',
          raw: {'TOPIAFORGE_DATA_ROOT': bytes},
        ),
        // In HOME: without HOME, Dart takes the default root from the
        // current directory, which makes the configured root the default.
        _Start(
          configured: '/home/u/.topiaforge_launcher',
          home: decoded == null ? null : '/home$decoded',
          raw: {
            'HOME': [...utf8.encode('/home'), ...bytes],
          },
        ),
      ]) {
        expect(
          runner.idFor(start) == _plainId,
          start.isDefaultToDart,
          reason: '$start',
        );
      }
    }
    // A character and its surrogate pair, each half encoded on its own, are
    // one string to Dart, so they name one root.
    final emoji = String.fromCharCode(0x1F600);
    final start = _Start(
      configured: '/home/$emoji/.topiaforge_launcher',
      home: '/home/$emoji',
      raw: {
        'HOME': [...utf8.encode('/home/'), ..._encodedSurrogatePair],
      },
    );
    expect(start.isDefaultToDart, isTrue, reason: '$start');
    expect(runner.idFor(start), _plainId, reason: '$start');
  }, skip: skip);

  test(
    'the decoding cases are what Dart makes of their bytes',
    () {
      final decodings = _decodings();
      final directory = Directory.systemTemp.createTempSync(
        'topiaforge-dart-environment-',
      );
      addTearDown(() => directory.deleteSync(recursive: true));
      final probe = File(p.join(directory.path, 'environment.dart'))
        ..writeAsStringSync(_environmentProbe);
      final result = Process.runSync('/bin/sh', [
        '-c',
        _rawEnvironmentScript,
        'sh',
        _dartExecutable()!,
        probe.path,
        for (final (bytes, _) in decodings) bytes.map(_octal).join(),
      ]);
      expect(result.exitCode, 0, reason: '${result.stderr}');
      final seen = {
        for (final line in LineSplitter.split(result.stdout as String))
          if (line.startsWith('raw ')) line.split(' ')[1]: line.split(' ')[2],
      };
      for (final (index, (bytes, decoded)) in decodings.indexed) {
        expect(
          seen['$index'],
          decoded == null ? '-' : decoded.codeUnits.join(','),
          reason: 'bytes ${_hex(bytes)}',
        );
      }
    },
    skip: _dartSkipReason(),
  );
}

/// Bytes a Dart string cannot carry, and the string Dart's
/// `Platform.environment` makes of each: null when Dart leaves the variable
/// out.
List<(List<int>, String?)> _decodings() => [
  ([0x2F, 0x63, 0x61, 0x66, 0xE9], null), // Latin-1
  ([0x2F, 0x63, 0x61, 0x66, 0xC3], null), // a truncated sequence
  ([0x2F, 0x80], null), // a stray continuation byte
  ([0x2F, 0xC0, 0xAF], null), // an overlong form
  ([0x2F, 0xF4, 0x90, 0x80, 0x80], null), // past U+10FFFF
  ([0x2F, 0xED, 0xA0, 0x80], '/${String.fromCharCode(0xD800)}'),
  ([0x2F, ..._encodedSurrogatePair], '/${String.fromCharCode(0x1F600)}'),
];

/// U+1F600 as its two surrogates, each encoded on its own.
const _encodedSurrogatePair = [0xED, 0xA0, 0xBD, 0xED, 0xB8, 0x80];

/// Sets `TOPIAFORGE_RAW_<n>` to each `printf %b` argument after the Dart VM
/// and the probe, then runs the probe with the count.
const _rawEnvironmentScript = r'''
dart=$1 probe=$2
shift 2
index=0
for octal in "$@"; do
  export "TOPIAFORGE_RAW_$index=$(printf %b "$octal")"
  index=$((index + 1))
done
exec "$dart" "$probe" "$index"
''';

/// Prints the code units Dart holds for each `TOPIAFORGE_RAW_<n>`, or `-`.
const _environmentProbe = r'''
import 'dart:io';

void main(List<String> arguments) {
  for (var index = 0; index < int.parse(arguments.single); index++) {
    final value = Platform.environment['TOPIAFORGE_RAW_$index'];
    print('raw $index ${value == null ? '-' : value.codeUnits.join(',')}');
  }
}
''';

/// The Dart VM of the Flutter SDK that runs these tests.
String? _dartExecutable() {
  final root = Platform.environment['FLUTTER_ROOT'];
  if (root == null) return null;
  final dart = p.join(root, 'bin', 'cache', 'dart-sdk', 'bin', 'dart');
  return File(dart).existsSync() ? dart : null;
}

String? _dartSkipReason() {
  if (!Platform.isLinux) return 'The Linux runner builds on Linux only.';
  if (_dartExecutable() == null) {
    return "Needs the Flutter SDK's Dart VM (FLUTTER_ROOT).";
  }
  return null;
}

String _hex(List<int> bytes) =>
    bytes.map((byte) => byte.toRadixString(16).padLeft(2, '0')).join();

/// [byte] as a `printf %b` octal escape.
String _octal(int byte) => '\\0${byte.toRadixString(8).padLeft(3, '0')}';

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
    this.raw = const {},
  });

  final String? configured;
  final String? home;
  final String? userProfile;
  final String currentDirectory;

  /// Variables the runner gets as these bytes, which a Dart string cannot
  /// carry. Their fields above hold what Dart makes of the bytes.
  final Map<String, List<int>> raw;

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
    for (final (name, value) in _variables)
      if (raw[name] case final bytes?)
        'x${_hex(bytes)}'
      else
        value == null ? '-' : '=$value',
    '=$currentDirectory',
  ];

  List<(String, String?)> get _variables => [
    ('TOPIAFORGE_DATA_ROOT', configured),
    ('HOME', home),
    ('USERPROFILE', userProfile),
  ];

  @override
  String toString() => [
    for (final (name, value) in _variables)
      '$name=${raw[name] == null ? _show(value) : 'bytes ${_hex(raw[name]!)}'}',
    'cwd=$currentDirectory',
  ].join(' ');

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
