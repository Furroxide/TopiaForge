import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  String windows(String root) => launcherInstanceScope(
    root,
    windows: true,
    currentDirectory: r'C:\Users\player',
  );
  String posix(String root) => launcherInstanceScope(
    root,
    windows: false,
    currentDirectory: '/home/player',
  );

  test('a scope is 32 lowercase hexadecimal characters', () {
    for (final scope in [
      windows(r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher'),
      posix('/home/player/.topiaforge_launcher'),
      launcherInstanceScope(Directory.systemTemp.path),
    ]) {
      expect(scope, matches(RegExp(r'^[0-9a-f]{32}$')));
    }
  });

  test('spellings of one Windows root share a scope', () {
    final scope = windows(
      r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher',
    );
    for (final spelling in [
      r'c:\users\PLAYER\appdata\roaming\topiaforgelauncher',
      r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher\',
      r'C:/Users/player/AppData/Roaming/TopiaForgeLauncher',
      r'C:\Users\player\AppData\Roaming\.\x\..\TopiaForgeLauncher',
      r'AppData\Roaming\TopiaForgeLauncher',
    ]) {
      expect(windows(spelling), scope, reason: spelling);
    }
  });

  test('spellings of one POSIX root share a scope, but case does not', () {
    final scope = posix('/home/player/.topiaforge_launcher');
    expect(posix('/home/player/.topiaforge_launcher/'), scope);
    expect(posix('/home/player/x/../.topiaforge_launcher'), scope);
    expect(posix('.topiaforge_launcher'), scope);
    expect(posix('/home/player/.TopiaForge_Launcher'), isNot(scope));
  });

  test('different data roots never share a scope', () {
    final roots = [
      r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher',
      r'C:\Users\other\AppData\Roaming\TopiaForgeLauncher',
      r'D:\Portable\TopiaForge\data',
      r'D:\Portable\TopiaForge\data2',
    ];
    expect(roots.map(windows).toSet(), hasLength(roots.length));
  });

  test('a scope is not the data root it names', () {
    final scope = windows(
      r'C:\Users\player\AppData\Roaming\TopiaForgeLauncher',
    );
    expect(scope, isNot(contains('player')));
    expect(SessionLink.hasScheme(scope), isFalse);
  });
}
