import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// Records every call, in order, across the setting and the registrar.
final class _Calls {
  final log = <String>[];
}

final class _FakeSetting implements SessionLinkSetting {
  _FakeSetting(this.calls);

  final _Calls calls;
  final _changes = StreamController<bool>.broadcast();
  bool enabled = true;
  Object? readError;
  Object? writeError;

  /// A change made elsewhere, as when the settings store saves one.
  void push(bool value) {
    enabled = value;
    _changes.add(value);
  }

  @override
  Future<bool> read() async {
    calls.log.add('read');
    final error = readError;
    if (error != null) throw error;
    return enabled;
  }

  @override
  Stream<bool> get changes => _changes.stream;

  @override
  Future<void> write(bool enabled) async {
    calls.log.add('write $enabled');
    final error = writeError;
    if (error != null) throw error;
    push(enabled);
  }
}

final class _FakeRegistrar implements SessionLinkRegistrar {
  _FakeRegistrar(this.calls);

  final _Calls calls;
  Object? error;

  /// When set, the next register call waits for it.
  Completer<void>? registerGate;

  @override
  Future<SessionLinkRegistrationResult> register() async {
    calls.log.add('register');
    final gate = registerGate;
    registerGate = null;
    if (gate != null) await gate.future;
    final failure = error;
    if (failure != null) throw failure;
    return const SessionLinkRegistrationResult(
      SessionLinkRegistration.registered,
    );
  }

  @override
  Future<SessionLinkRegistrationResult> unregister() async {
    calls.log.add('unregister');
    final failure = error;
    if (failure != null) throw failure;
    return const SessionLinkRegistrationResult(SessionLinkRegistration.removed);
  }
}

void main() {
  late _Calls calls;
  late _FakeSetting setting;
  late _FakeRegistrar registrar;
  late SessionLinkHandling handling;
  late List<SessionLinkHandlingStatus> statuses;

  setUp(() {
    calls = _Calls();
    setting = _FakeSetting(calls);
    registrar = _FakeRegistrar(calls);
    handling = SessionLinkHandling(setting: setting, registrar: registrar);
    statuses = [];
    handling.statuses.listen(statuses.add);
  });

  tearDown(() => handling.dispose());

  group('at start', () {
    test('registers while the setting is on', () async {
      final status = await handling.start();

      expect(calls.log, ['read', 'register']);
      expect(status.enabled, isTrue);
      expect(status.registration, SessionLinkRegistration.registered);
    });

    test('removes its own registration while the setting is off, so one a '
        'failed or unfinished removal left is gone', () async {
      setting.enabled = false;

      final status = await handling.start();

      expect(calls.log, ['read', 'unregister']);
      expect(status.enabled, isFalse);
      expect(status.registration, SessionLinkRegistration.removed);
    });

    test(
      'leaves the registration alone when the setting cannot be read',
      () async {
        setting.readError = const FormatException('damaged');

        final status = await handling.start();

        expect(calls.log, ['read']);
        expect(status.registration, SessionLinkRegistration.failed);
        expect(status.detail, contains('could not be read'));
      },
    );

    test('reports a registrar failure without throwing', () async {
      registrar.error = StateError('registry refused');

      final status = await handling.start();

      expect(calls.log, ['read', 'register']);
      expect(status.registration, SessionLinkRegistration.failed);
      expect(status.detail, contains('registry refused'));
    });

    test('starts once', () async {
      await handling.start();
      expect(handling.start, throwsStateError);
    });
  });

  group('when the setting changes', () {
    test('turning it off writes it, then removes the registration', () async {
      await handling.start();
      calls.log.clear();

      await handling.setEnabled(false);
      await pumpEventQueue();

      expect(calls.log, ['write false', 'unregister']);
      expect(statuses.single.enabled, isFalse);
      expect(statuses.single.registration, SessionLinkRegistration.removed);
    });

    test('turning it on writes it, then registers', () async {
      setting.enabled = false;
      await handling.start();
      calls.log.clear();

      await handling.setEnabled(true);
      await pumpEventQueue();

      expect(calls.log, ['write true', 'register']);
      expect(statuses.single.registration, SessionLinkRegistration.registered);
    });

    test('a change made elsewhere is followed too', () async {
      await handling.start();
      calls.log.clear();

      setting.push(false);
      await pumpEventQueue();

      expect(calls.log, ['unregister']);
      expect(statuses.single.enabled, isFalse);
    });

    test('a choice that cannot be saved changes nothing', () async {
      await handling.start();
      calls.log.clear();
      setting.writeError = StateError('disk full');

      await expectLater(handling.setEnabled(false), throwsStateError);
      await pumpEventQueue();

      expect(calls.log, ['write false']);
      expect(statuses, isEmpty);
    });

    test('a repeated value calls the registrar once', () async {
      await handling.start();
      calls.log.clear();

      setting
        ..push(false)
        ..push(false);
      await pumpEventQueue();

      expect(calls.log, ['unregister']);
      expect(statuses, hasLength(1));
    });

    test('calls run in order, so the registration ends where the setting '
        'ended', () async {
      final gate = Completer<void>();
      registrar.registerGate = gate;
      final started = handling.start();
      await pumpEventQueue();
      expect(calls.log, ['read', 'register']);

      setting.push(false);
      await pumpEventQueue();
      // The removal waits for the registration in flight.
      expect(calls.log, ['read', 'register']);

      gate.complete();
      expect((await started).registration, SessionLinkRegistration.registered);
      await pumpEventQueue();
      expect(calls.log, ['read', 'register', 'unregister']);
      expect(statuses.single.registration, SessionLinkRegistration.removed);
    });

    test('a change before start is applied by the start itself', () async {
      setting.push(false);

      final status = await handling.start();
      await pumpEventQueue();

      expect(calls.log, ['read', 'unregister']);
      expect(status.enabled, isFalse);
      expect(statuses, isEmpty);
    });

    test('stops following the setting once disposed', () async {
      await handling.start();
      await handling.dispose();
      calls.log.clear();

      setting.push(false);
      await pumpEventQueue();

      expect(calls.log, isEmpty);
    });
  });
}
