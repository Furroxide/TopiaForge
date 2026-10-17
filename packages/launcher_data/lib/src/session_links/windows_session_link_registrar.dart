import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'current_user_registry.dart';

/// Registers this launcher for `topiaforge://` links under
/// `HKEY_CURRENT_USER\Software\Classes\topiaforge`.
///
/// It writes the URL protocol marker, a default icon and an open command
/// that quotes the launcher's own path, `"<exe>" "%1"`, and only when the
/// registration is missing or names another program. Windows starts the
/// command directly, with the link as one argument, so no shell ever sees
/// it. Removal deletes the key only while it names this launcher. All of it
/// goes through [CurrentUserRegistry], which cannot reach
/// `HKEY_LOCAL_MACHINE`.
final class WindowsSessionLinkRegistrar implements SessionLinkRegistrar {
  WindowsSessionLinkRegistrar({
    required this.executable,
    CurrentUserRegistry? registry,
    this.classesRoot = r'Software\Classes',
  }) : _registry = registry;

  static const scheme = 'topiaforge';
  static const description = 'URL:TopiaForge session link';
  static const protocolValue = 'URL Protocol';

  /// The launcher the registration names.
  final String executable;

  /// Where the per-user classes live. Tests point it at a scratch key.
  final String classesRoot;

  final CurrentUserRegistry? _registry;
  late final CurrentUserRegistry _hive =
      _registry ?? WindowsCurrentUserRegistry();

  String get schemeKey => '$classesRoot\\$scheme';
  String get _iconKey => '$schemeKey\\DefaultIcon';
  String get _commandKey => '$schemeKey\\shell\\open\\command';

  /// The open command, `"<exe>" "%1"`.
  String get command => '"$executable" "%1"';
  String get _icon => '"$executable",0';

  @override
  Future<SessionLinkRegistrationResult> register() async {
    final problem = executableProblem(executable);
    if (problem != null) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: problem,
      );
    }
    try {
      if (_isCurrent()) {
        return const SessionLinkRegistrationResult(
          SessionLinkRegistration.alreadyRegistered,
        );
      }
      // The command goes last, so an interrupted write never leaves a
      // command behind without the rest.
      _hive.writeString(schemeKey, description);
      _hive.writeString(schemeKey, '', name: protocolValue);
      _hive.writeString(_iconKey, _icon);
      _hive.writeString(_commandKey, command);
      return const SessionLinkRegistrationResult(
        SessionLinkRegistration.registered,
      );
    } on Object catch (error) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: 'Windows did not accept the link registration: $error',
      );
    }
  }

  @override
  Future<SessionLinkRegistrationResult> unregister() async {
    try {
      final current = _hive.readString(_commandKey);
      if (current == null || !_sameText(current, command)) {
        return const SessionLinkRegistrationResult(
          SessionLinkRegistration.notRegistered,
        );
      }
      _hive.deleteTree(schemeKey);
      return const SessionLinkRegistrationResult(
        SessionLinkRegistration.removed,
      );
    } on Object catch (error) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: 'Windows did not remove the link registration: $error',
      );
    }
  }

  bool _isCurrent() {
    final protocol = _hive.readString(schemeKey, name: protocolValue);
    final icon = _hive.readString(_iconKey);
    final open = _hive.readString(_commandKey);
    return _hive.readString(schemeKey) == description &&
        protocol != null &&
        protocol.isEmpty &&
        icon != null &&
        _sameText(icon, _icon) &&
        open != null &&
        _sameText(open, command);
  }

  /// Windows paths ignore case, so a registration that differs only in case
  /// still names this launcher.
  static bool _sameText(String left, String right) =>
      left.toLowerCase() == right.toLowerCase();

  /// Why [executable] cannot be written into a quoted command, or null. A
  /// quote or control character could break out of the quoting.
  static String? executableProblem(String executable) {
    if (!p.windows.isAbsolute(executable) ||
        !RegExp(r'^(?:[A-Za-z]:\\|\\\\)').hasMatch(executable)) {
      return 'The launcher path is not an absolute Windows path.';
    }
    for (final unit in executable.codeUnits) {
      if (unit < 0x20 || unit == 0x22 || unit == 0x7F) {
        return 'The launcher path holds a character a command cannot quote.';
      }
    }
    return null;
  }
}
