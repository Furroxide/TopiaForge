@TestOn('windows')
library;

import 'dart:math';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// Runs the real registry calls against a scratch key,
/// `HKEY_CURRENT_USER\Software\TopiaForgeTests-<random>`, never against the
/// real `Software\Classes`, and deletes the whole key afterwards.
void main() {
  late WindowsCurrentUserRegistry registry;
  late String scratch;

  setUp(() {
    registry = WindowsCurrentUserRegistry();
    final random = Random.secure();
    final suffix = List.generate(
      8,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
    scratch = 'Software\\TopiaForgeTests-$suffix';
  });

  tearDown(() {
    registry.deleteTree(scratch);
    expect(registry.readString(scratch), isNull);
    expect(registry.readString('$scratch\\a\\b'), isNull);
  });

  test('writes, reads and overwrites default and named string values', () {
    final key = '$scratch\\a\\b';
    expect(registry.readString(key), isNull);

    registry.writeString(key, 'first');
    registry.writeString(key, '', name: 'Empty');
    registry.writeString(key, 'Ünïcødé ✓', name: 'Named');

    expect(registry.readString(key), 'first');
    expect(registry.readString(key, name: 'Empty'), '');
    expect(registry.readString(key, name: 'Named'), 'Ünïcødé ✓');
    expect(registry.readString(key, name: 'Missing'), isNull);

    registry.writeString(key, 'second');
    expect(registry.readString(key), 'second');
  });

  test('reads long values in full', () {
    final key = '$scratch\\long';
    final text = 'x' * 20000;
    registry.writeString(key, text);
    expect(registry.readString(key), text);
  });

  test('deletes a tree, and a missing tree is not an error', () {
    registry.writeString('$scratch\\tree\\leaf', 'value');
    registry.deleteTree('$scratch\\tree');
    expect(registry.readString('$scratch\\tree\\leaf'), isNull);
    registry.deleteTree('$scratch\\tree');
  });

  test('refuses paths that would name the whole hive', () {
    expect(() => registry.deleteTree(''), throwsArgumentError);
    expect(
      () => registry.deleteTree('Software\\\\Classes'),
      throwsArgumentError,
    );
    expect(() => registry.deleteTree('\\Software'), throwsArgumentError);
    expect(() => registry.readString('Software\\'), throwsArgumentError);
    expect(
      () => registry.writeString('$scratch\\nul', 'a\u0000b'),
      throwsArgumentError,
    );
  });

  test('the registrar round-trips through the real registry', () async {
    const launcher = r'C:\TopiaForge Test\launcher\topiaforge_launcher.exe';
    final registrar = WindowsSessionLinkRegistrar(
      executable: launcher,
      registry: registry,
      classesRoot: '$scratch\\Classes',
    );

    expect(
      (await registrar.register()).registration,
      SessionLinkRegistration.registered,
    );
    expect(
      registry.readString(
        '$scratch\\Classes\\topiaforge\\shell\\open\\command',
      ),
      '"$launcher" "%1"',
    );
    expect(
      registry.readString(
        '$scratch\\Classes\\topiaforge',
        name: 'URL Protocol',
      ),
      '',
    );
    expect(
      (await registrar.register()).registration,
      SessionLinkRegistration.alreadyRegistered,
    );
    expect(
      (await registrar.unregister()).registration,
      SessionLinkRegistration.removed,
    );
    expect(registry.readString('$scratch\\Classes\\topiaforge'), isNull);
  });
}
