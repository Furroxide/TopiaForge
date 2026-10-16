import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge_launcher_flutter/src/launcher_instance_channel.dart';
import 'package:topiaforge_launcher_flutter/src/update_health_handshake.dart';

/// The runners cannot be unit-tested from here, so this pins what Dart and
/// the three native runners must agree on, and the order of the Windows
/// checks on a link from another process.
void main() {
  final root = _launcherRoot().path;
  String read(List<String> parts) =>
      File(p.joinAll([root, ...parts])).readAsStringSync();

  final windows = read(['windows', 'runner', 'single_instance.cpp']);
  final windowsHeader = read(['windows', 'runner', 'single_instance.h']);
  final windowsChannel = read(['windows', 'runner', 'flutter_window.cpp']);
  final linux = read(['linux', 'runner', 'my_application.cc']);
  final macos = read(['macos', 'Runner', 'AppDelegate.swift']);
  final macosWindow = read(['macos', 'Runner', 'MainFlutterWindow.swift']);
  final plist = read(['macos', 'Runner', 'Info.plist']);

  test('every runner uses the Dart channel name', () {
    const name = '"${LauncherInstanceChannel.name}"';
    expect(windowsChannel, contains(name));
    expect(linux, contains(name));
    expect(macos, contains(name));
    expect(macosWindow, contains('SessionLinkChannel.shared.attach('));
  });

  test('every runner bounds a link at 512 characters plus a margin', () {
    expect(SessionLink.maximumLength, 512);
    expect(windowsHeader, contains('kMaximumLinkLength = 512 + 64;'));
    expect(linux, contains('kMaximumLinkLength = 512 + 64;'));
    expect(macos, contains('maximumLinkLength = 512 + 64'));
  });

  test('the Windows runner checks a link before it copies one', () {
    final receive = _between(
      windows,
      'bool SingleInstance::Receive(',
      'bool SingleInstance::Deliver(',
    );
    final scheme = receive.indexOf('HasSessionLinkScheme(text, length)');
    final bound = receive.indexOf('length > kMaximumLinkLength');
    final copy = receive.indexOf('ExactUtf8(text, length)');
    expect(scheme, greaterThan(0));
    expect(bound, greaterThan(scheme));
    expect(copy, greaterThan(bound));
    expect(windows, contains('L"topiaforge:"'));
    expect(
      read(['windows', 'runner', 'CMakeLists.txt']),
      contains('"single_instance.cpp"'),
    );
  });

  test('a Windows link start passes the raw command line as its link', () {
    final main = read(['windows', 'runner', 'main.cpp']);
    final link = main.indexOf(
      'SingleInstance::LinkFromCommandLine(::GetCommandLineW())',
    );
    final split = main.indexOf('GetCommandLineArguments()');
    expect(link, greaterThan(0));
    expect(split, greaterThan(link));
    expect(main, contains('return static_cast<int>(msg.wParam);'));
    // A link that does not convert still arrives as a link, and is refused.
    expect(main, contains('text.empty() ? "topiaforge:"'));
    expect(SessionLink.read('topiaforge:').refusal, SessionLinkRefusal.invalid);
  });

  test(
    'a closing Windows launcher gives its scope up before anything else',
    () {
      final destroy = _between(
        windowsChannel,
        'void FlutterWindow::OnDestroy()',
        'LRESULT',
      );
      final release = destroy.indexOf('single_instance_.Release();');
      expect(release, greaterThan(0));
      expect(
        destroy.indexOf('single_instance_.SetHandlers(nullptr, nullptr);'),
        greaterThan(release),
      );
      final deliver = _between(
        windows,
        'bool SingleInstance::Deliver(',
        'void SingleInstance::BringForward()',
      );
      // A link nothing can take any more is not acknowledged.
      expect(deliver, contains('if (released_) {\n    return false;'));
      expect(deliver, contains('if (!handler) {\n    return false;'));
      expect(windows, contains('return delivered;'));
      // Win32Window::Create runs OnDestroy before anything is claimed, so a
      // release with nothing held must leave the claim possible.
      expect(
        _between(windows, 'void SingleInstance::Release()', 'released_ = '),
        contains('if (window_ == nullptr && mutex_ == nullptr) {\n    return;'),
      );
    },
  );

  test('the Windows runner never sizes text from a failed conversion', () {
    final utils = read(['windows', 'runner', 'utils.cpp']);
    expect(utils, contains('const int target_length = '));
    expect(utils, contains('if (target_length <= 0) {'));
    expect(utils, isNot(contains('unsigned int target_length')));
  });

  test('the Linux runner is unique over D-Bus and knows health starts', () {
    expect(
      RegExp(
        r'"flags",\s*G_APPLICATION_HANDLES_COMMAND_LINE,\s*nullptr\)',
      ).hasMatch(linux),
      isTrue,
    );
    expect(linux, contains('"$updateHealthNonceOption"'));
    expect(linux, contains('"$updateHealthFileOption"'));
    expect(linux, contains('"topiaforge:"'));
    // Only the helper's exact five-element argv is a health start, and a
    // link start is its first argument alone.
    expect(linux, contains('g_strv_length(arguments) == 5'));
    expect(
      _between(linux, 'session_link_argument(gchar** arguments) {', '\n}'),
      isNot(contains('for (')),
    );
  });

  test('the Linux id follows the data root the way Dart resolves it', () {
    // linux_application_id_test.dart holds data_root_id.cc to Dart on a
    // Linux host; this pins how the runner feeds it.
    final rootId = read(['linux', 'runner', 'data_root_id.cc']);
    expect(linux, contains('data_root_application_id('));
    // Directory.current is getcwd(): symbolic links resolved, never $PWD.
    expect(linux, contains('getcwd(nullptr, 0)'));
    for (final glibOnly in [
      'g_get_current_dir',
      'g_canonicalize_filename',
      'g_strstrip',
    ]) {
      expect(linux, isNot(contains(glibOnly)), reason: glibOnly);
      expect(rootId, isNot(contains(glibOnly)), reason: glibOnly);
    }
    // The whitespace String.trim() removes.
    for (final unit in [
      '0x09',
      '0x0D',
      '0x20',
      '0x85',
      '0xA0',
      '0x1680',
      '0x2000',
      '0x200A',
      '0x2028',
      '0x2029',
      '0x202F',
      '0x205F',
      '0x3000',
      '0xFEFF',
    ]) {
      expect(rootId, contains(unit), reason: unit);
    }
    expect(rootId, contains('strcmp(key, fallback) == 0'));
    expect(
      read(['linux', 'runner', 'CMakeLists.txt']),
      contains('"data_root_id.cc"'),
    );
  });

  test('the macOS bundle declares the topiaforge scheme', () {
    expect(
      RegExp(
        r'<key>CFBundleURLSchemes</key>\s*<array>\s*'
        r'<string>topiaforge</string>\s*</array>',
      ).hasMatch(plist),
      isTrue,
    );
    expect(macos, contains('override func application('));
  });
}

/// The text of [source] from [start] up to the next [end] after it.
String _between(String source, String start, String end) {
  final from = source.indexOf(start);
  expect(from, greaterThanOrEqualTo(0), reason: 'missing $start');
  final to = source.indexOf(end, from + start.length);
  return source.substring(from, to < 0 ? source.length : to);
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
    final parent = current.parent;
    if (parent.path == current.path) {
      throw StateError('Could not locate the launcher project.');
    }
    current = parent;
  }
}
