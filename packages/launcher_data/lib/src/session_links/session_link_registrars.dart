import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import 'linux_session_link_registrar.dart';
import 'windows_session_link_registrar.dart';

/// The registrar for this platform: the current user's registry on Windows,
/// a desktop entry on Linux, and the application bundle on macOS. It names
/// [executable], by default the running launcher.
SessionLinkRegistrar platformSessionLinkRegistrar({String? executable}) {
  final launcher = executable ?? Platform.resolvedExecutable;
  if (Platform.isWindows) {
    return WindowsSessionLinkRegistrar(executable: launcher);
  }
  if (Platform.isLinux) return LinuxSessionLinkRegistrar(executable: launcher);
  if (Platform.isMacOS) return const BundleSessionLinkRegistrar();
  return const UnsupportedSessionLinkRegistrar();
}

/// macOS: `CFBundleURLTypes` in the application bundle declares the scheme,
/// and LaunchServices keeps the registration. Nothing is written or removed.
final class BundleSessionLinkRegistrar implements SessionLinkRegistrar {
  const BundleSessionLinkRegistrar();

  static const _result = SessionLinkRegistrationResult(
    SessionLinkRegistration.managedBySystem,
    detail:
        'macOS registers topiaforge:// links from the application bundle, so '
        'this setting cannot remove the registration.',
  );

  @override
  Future<SessionLinkRegistrationResult> register() async => _result;

  @override
  Future<SessionLinkRegistrationResult> unregister() async => _result;
}

/// A platform without run-time registration.
final class UnsupportedSessionLinkRegistrar implements SessionLinkRegistrar {
  const UnsupportedSessionLinkRegistrar();

  static const _result = SessionLinkRegistrationResult(
    SessionLinkRegistration.unsupported,
    detail: 'This platform cannot register topiaforge:// links.',
  );

  @override
  Future<SessionLinkRegistrationResult> register() async => _result;

  @override
  Future<SessionLinkRegistrationResult> unregister() async => _result;
}
