import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Answers `xdg-mime` the way the real one would, and records every call.
final class _FakeXdgMime {
  String defaultHandler = '';
  int defaultExitCode = 0;
  bool missing = false;
  final calls = <List<String>>[];

  Future<BoundedProcessResult> run(
    String executable,
    List<String> arguments,
  ) async {
    calls.add([executable, ...arguments]);
    if (missing) throw ProcessException(executable, arguments, 'not found');
    if (arguments.first == 'query') {
      return BoundedProcessResult(
        exitCode: 0,
        stdout: defaultHandler.isEmpty ? '' : '$defaultHandler\n',
        stderr: '',
      );
    }
    if (defaultExitCode == 0) defaultHandler = arguments[1];
    return BoundedProcessResult(
      exitCode: defaultExitCode,
      stdout: '',
      stderr: '',
    );
  }
}

void main() {
  const launcher = '/opt/TopiaForge Launcher/topiaforge_launcher';
  const name = LinuxSessionLinkRegistrar.desktopFileName;
  late Directory root;
  late _FakeXdgMime xdg;
  late Map<String, String> environment;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('topiaforge-xdg-');
    xdg = _FakeXdgMime();
    environment = {'XDG_DATA_HOME': p.join(root.path, 'data')};
  });

  tearDown(() => root.delete(recursive: true));

  LinuxSessionLinkRegistrar registrar([String executable = launcher]) =>
      LinuxSessionLinkRegistrar(
        executable: executable,
        environment: environment,
        runCommand: xdg.run,
      );

  File entry() => File(p.join(root.path, 'data', 'applications', name));

  test(
    'writes a hidden scheme-handler entry and makes it the default',
    () async {
      final result = await registrar().register();

      expect(result.registration, SessionLinkRegistration.registered);
      expect(
        entry().readAsStringSync(),
        '[Desktop Entry]\n'
        'Type=Application\n'
        'Name=TopiaForge\n'
        'Comment=Opens topiaforge:// session links in the TopiaForge launcher\n'
        'Exec="$launcher" %u\n'
        'Terminal=false\n'
        'NoDisplay=true\n'
        'MimeType=x-scheme-handler/topiaforge;\n',
      );
      expect(xdg.calls.last, [
        'xdg-mime',
        'default',
        name,
        'x-scheme-handler/topiaforge',
      ]);
      expect(xdg.defaultHandler, name);
    },
  );

  test(
    'does nothing more when the entry and the default are current',
    () async {
      await registrar().register();
      final modified = entry().lastModifiedSync();
      xdg.calls.clear();

      final result = await registrar().register();

      expect(result.registration, SessionLinkRegistration.alreadyRegistered);
      expect(xdg.calls, [
        ['xdg-mime', 'query', 'default', 'x-scheme-handler/topiaforge'],
      ]);
      expect(entry().lastModifiedSync(), modified);
    },
  );

  test('takes the default back when another handler holds it', () async {
    await registrar().register();
    xdg.defaultHandler = 'other-launcher.desktop';

    final result = await registrar().register();

    expect(result.registration, SessionLinkRegistration.registered);
    expect(xdg.defaultHandler, name);
  });

  test('rewrites an entry that names another launcher', () async {
    await registrar('/elsewhere/topiaforge_launcher').register();

    final result = await registrar().register();

    expect(result.registration, SessionLinkRegistration.registered);
    expect(entry().readAsStringSync(), contains('Exec="$launcher" %u'));
  });

  test('falls back to HOME when XDG_DATA_HOME is unset or relative', () async {
    final home = p.join(root.path, 'home');
    for (final dataHome in [null, '', 'relative/data']) {
      environment = {'HOME': home, 'XDG_DATA_HOME': ?dataHome};
      expect(
        registrar().applicationsDirectory,
        p.join(home, '.local', 'share', 'applications'),
      );
    }
    environment = {'HOME': 'relative'};
    expect(registrar().applicationsDirectory, isNull);
    expect(
      (await registrar().register()).registration,
      SessionLinkRegistration.failed,
    );
  });

  test('refuses a launcher path that an Exec line cannot quote', () async {
    for (final path in [
      'relative/topiaforge_launcher',
      '/opt/a"b/topiaforge_launcher',
      r'/opt/$HOME/topiaforge_launcher',
      '/opt/a`b`/topiaforge_launcher',
      r'/opt/back\slash/topiaforge_launcher',
      '/opt/100%/topiaforge_launcher',
      '/opt/line\nbreak/topiaforge_launcher',
    ]) {
      final result = await registrar(path).register();
      expect(result.registration, SessionLinkRegistration.failed, reason: path);
    }
    expect(xdg.calls, isEmpty);
    expect(entry().existsSync(), isFalse);
  });

  test('a sandboxed package cannot register for the host', () async {
    for (final sandbox in ['SNAP', 'FLATPAK_ID']) {
      environment = {...environment, sandbox: 'topiaforge'};
      expect(
        (await registrar().register()).registration,
        SessionLinkRegistration.unsupported,
      );
      expect(
        (await registrar().unregister()).registration,
        SessionLinkRegistration.unsupported,
      );
      environment.remove(sandbox);
    }
    expect(xdg.calls, isEmpty);
  });

  test('reports a missing or failing xdg-mime', () async {
    xdg.missing = true;
    final missing = await registrar().register();
    expect(missing.registration, SessionLinkRegistration.failed);
    expect(missing.detail, contains('xdg-mime'));

    xdg
      ..missing = false
      ..defaultExitCode = 4;
    expect(
      (await registrar().register()).registration,
      SessionLinkRegistration.failed,
    );
  });

  test('removal deletes the entry while it names this launcher', () async {
    await registrar().register();

    final result = await registrar().unregister();

    expect(result.registration, SessionLinkRegistration.removed);
    expect(entry().existsSync(), isFalse);
  });

  test('removal leaves another launcher\'s entry alone', () async {
    await registrar('/elsewhere/topiaforge_launcher').register();

    final result = await registrar().unregister();

    expect(result.registration, SessionLinkRegistration.notRegistered);
    expect(entry().existsSync(), isTrue);
    expect(
      (await registrar().unregister()).registration,
      SessionLinkRegistration.notRegistered,
    );
  });
}
