import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// An in-memory hive that records every write and delete.
final class _MemoryRegistry implements CurrentUserRegistry {
  final values = <String, String>{};
  final writes = <String>[];
  final deletes = <String>[];
  Object? failure;

  static String _slot(String keyPath, String? name) =>
      '${keyPath.toLowerCase()}|${name ?? ''}';

  @override
  String? readString(String keyPath, {String? name}) {
    final error = failure;
    if (error != null) throw error;
    return values[_slot(keyPath, name)];
  }

  @override
  void writeString(String keyPath, String value, {String? name}) {
    final error = failure;
    if (error != null) throw error;
    writes.add(_slot(keyPath, name));
    values[_slot(keyPath, name)] = value;
  }

  @override
  void deleteTree(String keyPath) {
    final error = failure;
    if (error != null) throw error;
    deletes.add(keyPath);
    final prefix = keyPath.toLowerCase();
    values.removeWhere(
      (slot, _) => slot.startsWith('$prefix|') || slot.startsWith('$prefix\\'),
    );
  }
}

void main() {
  const launcher =
      r'C:\Program Files\TopiaForge\launcher\topiaforge_launcher.exe';
  const other = r'D:\Other\topiaforge_launcher.exe';
  const scheme = r'Software\Classes\topiaforge';
  late _MemoryRegistry registry;

  WindowsSessionLinkRegistrar registrar([String executable = launcher]) =>
      WindowsSessionLinkRegistrar(executable: executable, registry: registry);

  String? value(String keyPath, {String? name}) =>
      registry.readString(keyPath, name: name);

  setUp(() => registry = _MemoryRegistry());

  test('registers the scheme, icon and quoted command when missing', () async {
    final result = await registrar().register();

    expect(result.registration, SessionLinkRegistration.registered);
    expect(value(scheme), 'URL:TopiaForge session link');
    expect(value(scheme, name: 'URL Protocol'), '');
    expect(value('$scheme\\DefaultIcon'), '"$launcher",0');
    expect(value('$scheme\\shell\\open\\command'), '"$launcher" "%1"');
    // The command goes last, after everything it depends on.
    expect(
      registry.writes.last,
      '${scheme.toLowerCase()}\\shell\\open\\command|',
    );
  });

  test('writes only under the per-user classes key it is given', () async {
    await registrar().register();
    for (final slot in registry.writes) {
      expect(slot, startsWith(r'software\classes\topiaforge'));
    }

    registry = _MemoryRegistry();
    final scratch = WindowsSessionLinkRegistrar(
      executable: launcher,
      registry: registry,
      classesRoot: r'Software\TopiaForgeTests\scratch',
    );
    await scratch.register();
    expect(registry.writes, hasLength(4));
    for (final slot in registry.writes) {
      expect(slot, startsWith(r'software\topiaforgetests\scratch\topiaforge'));
    }
  });

  test(
    'does not write when the registration already names this launcher',
    () async {
      await registrar().register();
      registry.writes.clear();

      final result = await registrar().register();

      expect(result.registration, SessionLinkRegistration.alreadyRegistered);
      expect(registry.writes, isEmpty);
    },
  );

  test('a registration that differs only in case still counts', () async {
    await registrar().register();
    registry.writes.clear();

    final result = await registrar(launcher.toUpperCase()).register();

    expect(result.registration, SessionLinkRegistration.alreadyRegistered);
    expect(registry.writes, isEmpty);
  });

  test('takes over a registration that names another program', () async {
    await registrar(other).register();

    final result = await registrar().register();

    expect(result.registration, SessionLinkRegistration.registered);
    expect(value('$scheme\\shell\\open\\command'), '"$launcher" "%1"');
    expect(value('$scheme\\DefaultIcon'), '"$launcher",0');
  });

  test('repairs a registration that lost a part', () async {
    await registrar().register();
    registry.values.remove('${scheme.toLowerCase()}|URL Protocol');

    final result = await registrar().register();

    expect(result.registration, SessionLinkRegistration.registered);
    expect(value(scheme, name: 'URL Protocol'), '');
  });

  test('removal deletes the key while it names this launcher', () async {
    await registrar().register();

    final result = await registrar().unregister();

    expect(result.registration, SessionLinkRegistration.removed);
    expect(registry.deletes, [scheme]);
    expect(value('$scheme\\shell\\open\\command'), isNull);
  });

  test('removal leaves another program\'s registration alone', () async {
    await registrar(other).register();

    final result = await registrar().unregister();

    expect(result.registration, SessionLinkRegistration.notRegistered);
    expect(registry.deletes, isEmpty);
    expect(value('$scheme\\shell\\open\\command'), '"$other" "%1"');
  });

  test('removal with nothing registered changes nothing', () async {
    final result = await registrar().unregister();

    expect(result.registration, SessionLinkRegistration.notRegistered);
    expect(registry.deletes, isEmpty);
  });

  test('refuses a launcher path that a command cannot quote', () async {
    for (final path in [
      r'relative\topiaforge_launcher.exe',
      r'\no-drive\topiaforge_launcher.exe',
      'C:\\Quote"d\\topiaforge_launcher.exe',
      'C:\\Line\nBreak\\topiaforge_launcher.exe',
    ]) {
      final result = await registrar(path).register();
      expect(result.registration, SessionLinkRegistration.failed, reason: path);
    }
    expect(registry.writes, isEmpty);
    expect(
      WindowsSessionLinkRegistrar.executableProblem(
        r'\\server\share\topiaforge_launcher.exe',
      ),
      isNull,
    );
  });

  test('reports registry failures instead of throwing', () async {
    registry.failure = const CurrentUserRegistryException('write', 5);

    expect(
      (await registrar().register()).registration,
      SessionLinkRegistration.failed,
    );
    expect(
      (await registrar().unregister()).registration,
      SessionLinkRegistration.failed,
    );
  });
}
