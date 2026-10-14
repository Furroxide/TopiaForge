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
    final receive = windows.substring(
      windows.indexOf('bool SingleInstance::Receive('),
      windows.indexOf('void SingleInstance::Deliver('),
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
