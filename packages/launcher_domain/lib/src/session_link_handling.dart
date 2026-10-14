/// Whether the launcher opens `topiaforge://` links, and how the operating
/// system's record of that stands.
///
/// Releases ship no installer, so the launcher registers itself at run time:
/// at start while the player's setting is on, and it removes its own
/// registration when the player turns the setting off. The setting governs
/// only that registration. A link that still reaches the launcher, as on
/// macOS where the application bundle declares the scheme, opens the consent
/// flow like any other and never joins by itself.
library;

import 'dart:async';

/// The player's link-handling choice, `MultiplayerSettings.linkHandlingEnabled`
/// in the launcher's multiplayer settings. Registration reads it through this
/// view and follows its changes wherever they are made.
abstract interface class SessionLinkSetting {
  /// The current choice.
  Future<bool> read();

  /// Every later change of the choice.
  Stream<bool> get changes;

  /// Changes the choice, which then arrives on [changes]. Throws when it
  /// cannot be saved.
  Future<void> write(bool enabled);
}

/// Where the system's `topiaforge://` registration stands after a
/// [SessionLinkRegistrar] call.
enum SessionLinkRegistration {
  /// It was missing or named another program, and now names this launcher.
  registered,

  /// It already named this launcher, so nothing was written.
  alreadyRegistered,

  /// It named this launcher and was removed.
  removed,

  /// Nothing of this launcher's was there to remove. A registration that
  /// names another program is left alone.
  notRegistered,

  /// The application bundle declares the scheme, so the system keeps the
  /// registration and the launcher neither writes nor removes it (macOS).
  managedBySystem,

  /// This platform or package cannot register at run time, as inside a snap
  /// or Flatpak sandbox.
  unsupported,

  /// The setting or the registration could not be read or written; the
  /// detail says why.
  failed,
}

/// The outcome of one registrar call. [detail] is for people.
final class SessionLinkRegistrationResult {
  const SessionLinkRegistrationResult(this.registration, {this.detail = ''});

  final SessionLinkRegistration registration;
  final String detail;
}

/// The operating system's record of which program opens `topiaforge://`
/// links. Implementations touch only the current user's registration.
abstract interface class SessionLinkRegistrar {
  /// Makes this launcher the handler. It writes only when the registration
  /// is missing or names another program.
  Future<SessionLinkRegistrationResult> register();

  /// Removes the registration when it names this launcher.
  Future<SessionLinkRegistrationResult> unregister();
}

/// The setting, and what the registrar last made of it.
final class SessionLinkHandlingStatus {
  const SessionLinkHandlingStatus({
    required this.enabled,
    this.registration,
    this.detail = '',
  });

  final bool enabled;

  /// What the registrar reported, or null when it was not asked because the
  /// setting was off at start.
  final SessionLinkRegistration? registration;

  /// For people; empty when there is nothing to add.
  final String detail;
}

/// What the launcher asks of link handling.
abstract interface class SessionLinkHandlingGateway {
  /// Applies the setting at start, then follows its changes.
  Future<SessionLinkHandlingStatus> start();

  /// The status after each change of the setting, once started.
  Stream<SessionLinkHandlingStatus> get statuses;

  /// Changes the player's choice. The registration follows, and its status
  /// arrives on [statuses]. Throws when the choice cannot be saved.
  Future<void> setEnabled(bool enabled);
}

/// The rule between the setting and the registrar. At start it registers
/// only while the setting is on, and does not call the registrar otherwise.
/// Afterwards it registers or removes as the setting changes, one call at a
/// time and in order, so the registration always ends where the setting
/// ended. A registrar failure is reported, never thrown.
final class SessionLinkHandling implements SessionLinkHandlingGateway {
  SessionLinkHandling({
    required SessionLinkSetting setting,
    required SessionLinkRegistrar registrar,
  }) : _setting = setting,
       _registrar = registrar;

  final SessionLinkSetting _setting;
  final SessionLinkRegistrar _registrar;
  final _statuses = StreamController<SessionLinkHandlingStatus>.broadcast();
  StreamSubscription<bool>? _changes;
  Future<void> _queue = Future<void>.value();
  bool? _applied;

  @override
  Stream<SessionLinkHandlingStatus> get statuses => _statuses.stream;

  @override
  Future<SessionLinkHandlingStatus> start() {
    if (_changes != null) throw StateError('Link handling already started.');
    // Listen before reading, so a change made meanwhile is applied after it.
    _changes = _setting.changes.listen(_follow);
    return _enqueue(_startOnce);
  }

  @override
  Future<void> setEnabled(bool enabled) => _setting.write(enabled);

  /// Stops following the setting. The registration stays as it is.
  Future<void> dispose() async {
    await _changes?.cancel();
    await _queue;
    await _statuses.close();
  }

  Future<SessionLinkHandlingStatus> _startOnce() async {
    final bool enabled;
    try {
      enabled = await _setting.read();
    } on Object catch (error) {
      // An unreadable setting may hold the player's "off", so the
      // registration is left exactly as it is.
      return SessionLinkHandlingStatus(
        enabled: true,
        registration: SessionLinkRegistration.failed,
        detail: 'The link setting could not be read: $error',
      );
    }
    _applied = enabled;
    if (!enabled) return const SessionLinkHandlingStatus(enabled: false);
    return _apply(true);
  }

  void _follow(bool enabled) {
    _enqueue(() async {
      if (_applied == enabled) return null;
      _applied = enabled;
      return _apply(enabled);
    }).then((status) {
      if (status != null && !_statuses.isClosed) _statuses.add(status);
    });
  }

  Future<T> _enqueue<T>(Future<T> Function() step) {
    final result = _queue.then((_) => step());
    _queue = result.then<void>((_) {}, onError: (Object _) {});
    return result;
  }

  Future<SessionLinkHandlingStatus> _apply(bool enabled) async {
    try {
      final result = enabled
          ? await _registrar.register()
          : await _registrar.unregister();
      return SessionLinkHandlingStatus(
        enabled: enabled,
        registration: result.registration,
        detail: result.detail,
      );
    } on Object catch (error) {
      return SessionLinkHandlingStatus(
        enabled: enabled,
        registration: SessionLinkRegistration.failed,
        detail: '$error',
      );
    }
  }
}
