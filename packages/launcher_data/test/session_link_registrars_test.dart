import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('the platform registrar matches the host', () {
    final registrar = platformSessionLinkRegistrar(
      executable: Platform.isWindows
          ? r'C:\TopiaForge\launcher\topiaforge_launcher.exe'
          : '/opt/topiaforge/launcher/topiaforge_launcher',
    );
    if (Platform.isWindows) {
      expect(registrar, isA<WindowsSessionLinkRegistrar>());
      expect(
        (registrar as WindowsSessionLinkRegistrar).executable,
        r'C:\TopiaForge\launcher\topiaforge_launcher.exe',
      );
    } else if (Platform.isLinux) {
      expect(registrar, isA<LinuxSessionLinkRegistrar>());
    } else if (Platform.isMacOS) {
      expect(registrar, isA<BundleSessionLinkRegistrar>());
    } else {
      expect(registrar, isA<UnsupportedSessionLinkRegistrar>());
    }
  });

  test('the platform registrar names the running launcher by default', () {
    final registrar = platformSessionLinkRegistrar();
    if (registrar is WindowsSessionLinkRegistrar) {
      expect(registrar.executable, Platform.resolvedExecutable);
    } else if (registrar is LinuxSessionLinkRegistrar) {
      expect(registrar.executable, Platform.resolvedExecutable);
    }
  });

  test(
    'a launcher on its own data root never touches the registration',
    () async {
      final scratch = await Directory.systemTemp.createTemp(
        'topiaforge-own-root-',
      );
      addTearDown(() => scratch.delete(recursive: true));

      final registrar = platformSessionLinkRegistrar(
        executable: Platform.isWindows
            ? r'C:\TopiaForge\launcher\topiaforge_launcher.exe'
            : '/opt/topiaforge/launcher/topiaforge_launcher',
        dataRoot: scratch.path,
      );

      if (Platform.isMacOS) {
        expect(registrar, isA<BundleSessionLinkRegistrar>());
        return;
      }
      expect(registrar, isA<OwnDataRootSessionLinkRegistrar>());
      for (final result in [
        await registrar.register(),
        await registrar.unregister(),
      ]) {
        expect(result.registration, SessionLinkRegistration.unsupported);
        expect(result.detail, contains('own data root'));
      }
    },
  );

  test('the launcher on the default data root registers', () {
    final registrar = platformSessionLinkRegistrar(
      dataRoot: resolveTopiaForgeDataRoot(
        environment: {...Platform.environment}..remove('TOPIAFORGE_DATA_ROOT'),
      ),
    );
    if (Platform.isWindows) {
      expect(registrar, isA<WindowsSessionLinkRegistrar>());
    } else if (Platform.isLinux) {
      expect(registrar, isA<LinuxSessionLinkRegistrar>());
    }
  });

  test('macOS leaves the bundle registration to the system', () async {
    const registrar = BundleSessionLinkRegistrar();
    for (final result in [
      await registrar.register(),
      await registrar.unregister(),
    ]) {
      expect(result.registration, SessionLinkRegistration.managedBySystem);
      expect(result.detail, contains('application bundle'));
    }
  });

  test('other platforms report that they cannot register', () async {
    const registrar = UnsupportedSessionLinkRegistrar();
    expect(
      (await registrar.register()).registration,
      SessionLinkRegistration.unsupported,
    );
    expect(
      (await registrar.unregister()).registration,
      SessionLinkRegistration.unsupported,
    );
  });
}
