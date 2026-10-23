import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_unreadable_settings.dart';

/// A registrar that records what it was asked, as the system's would see it.
final class _RecordingRegistrar implements SessionLinkRegistrar {
  final calls = <String>[];

  @override
  Future<SessionLinkRegistrationResult> register() async {
    calls.add('register');
    return const SessionLinkRegistrationResult(
      SessionLinkRegistration.registered,
    );
  }

  @override
  Future<SessionLinkRegistrationResult> unregister() async {
    calls.add('unregister');
    return const SessionLinkRegistrationResult(SessionLinkRegistration.removed);
  }
}

/// The launcher's link handling as `main.dart` puts it together: one store,
/// the setting it backs, and the registration that follows the setting.
final class _Launcher {
  _Launcher(String folder)
    : store = MultiplayerStore(folder),
      registrar = _RecordingRegistrar() {
    setting = StoredSessionLinkSetting(store);
    handling = SessionLinkHandling(setting: setting, registrar: registrar);
    handling.statuses.listen(statuses.add);
  }

  final MultiplayerStore store;
  final _RecordingRegistrar registrar;
  late final StoredSessionLinkSetting setting;
  late final SessionLinkHandling handling;
  final statuses = <SessionLinkHandlingStatus>[];

  Future<void> close() async {
    await handling.dispose();
    await setting.close();
  }
}

void main() {
  late Directory root;
  late String folder;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-link-setting-');
    folder = p.join(root.path, 'multiplayer');
  });
  tearDown(() => root.deleteSync(recursive: true));

  _Launcher launcher() {
    final started = _Launcher(folder);
    addTearDown(started.close);
    return started;
  }

  Map<String, Object?> stored() =>
      jsonDecode(
            File(
              p.join(folder, MultiplayerStore.settingsFileName),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;

  test('is on by default, as the plan sets it', () async {
    final first = launcher();
    expect(await first.setting.read(), isTrue);
    final status = await first.handling.start();
    expect(status.enabled, isTrue);
    expect(first.registrar.calls, ['register']);
  });

  test('"off" survives a restart, and the registration follows it', () async {
    final first = launcher();
    await first.handling.start();
    await first.handling.setEnabled(false);
    await pumpEventQueue();
    expect(first.registrar.calls, ['register', 'unregister']);
    expect(first.statuses.last.enabled, isFalse);
    expect(first.statuses.last.registration, SessionLinkRegistration.removed);
    expect(stored()['linkHandlingEnabled'], isFalse);
    await first.close();

    // The launcher starts again over the same folder: "off" was kept, so it
    // neither registers nor removes anything.
    final second = launcher();
    final status = await second.handling.start();
    expect(status.enabled, isFalse);
    expect(status.registration, isNull);
    await pumpEventQueue();
    expect(second.registrar.calls, isEmpty);
    expect(second.statuses, isEmpty);

    // Turning it on again registers, once.
    await second.handling.setEnabled(true);
    await pumpEventQueue();
    expect(second.registrar.calls, ['register']);
    expect(stored()['linkHandlingEnabled'], isTrue);
    await second.handling.setEnabled(true);
    await pumpEventQueue();
    expect(second.registrar.calls, ['register']);
  });

  test(
    'a change saved anywhere else in the store reaches the registration',
    () async {
      final started = launcher();
      await started.handling.start();
      await started.store.updateSettings(
        (settings) => settings.withLinkHandling(false).withDisplayName('Sam'),
      );
      await pumpEventQueue();
      expect(started.registrar.calls, ['register', 'unregister']);
      expect(stored()['displayName'], 'Sam');
    },
  );

  test(
    'settings that cannot be read change nothing on this computer',
    () async {
      // The player turned links off in an earlier session.
      await MultiplayerStore(
        folder,
      ).saveSettings(MultiplayerSettings(linkHandlingEnabled: false));
      final before = stored();
      final started = launcher();

      final status = await whileSettingsUnreadable(() async {
        final status = await started.handling.start();
        // A change asked for meanwhile is refused, not saved over the file.
        await expectLater(
          started.handling.setEnabled(true),
          throwsA(
            isA<MultiplayerStoreException>().having(
              (error) => error.unreadable,
              'unreadable',
              isTrue,
            ),
          ),
        );
        await expectLater(
          started.setting.read(),
          throwsA(isA<MultiplayerStoreException>()),
        );
        await pumpEventQueue();
        return status;
      });

      // The stand-ins have link handling off; following them would remove the
      // player's registration. Nothing was asked of the registrar.
      expect(status.registration, SessionLinkRegistration.failed);
      expect(status.detail, contains('could not be read'));
      expect(started.registrar.calls, isEmpty);
      expect(started.statuses, isEmpty);
      expect(stored(), before);

      // Once the settings read again, the registration follows what they say.
      await started.store.loadSettings();
      await pumpEventQueue();
      expect(started.registrar.calls, ['unregister']);
      expect(started.statuses.single.enabled, isFalse);
    },
  );

  test(
    'settings that read again after an unreadable start are followed',
    () async {
      await MultiplayerStore(folder).saveSettings(MultiplayerSettings());
      final started = launcher();
      final status = await whileSettingsUnreadable(
        () => started.handling.start(),
      );
      expect(status.registration, SessionLinkRegistration.failed);
      expect(started.registrar.calls, isEmpty);

      // They hold link handling on: once they read, it registers.
      await started.store.loadSettings();
      await pumpEventQueue();
      expect(started.registrar.calls, ['register']);
      expect(started.statuses.single.enabled, isTrue);
    },
  );
}
