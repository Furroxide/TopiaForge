import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import '../data_root.dart';
import 'linux_session_link_registrar.dart';
import 'windows_session_link_registrar.dart';

/// The registrar for this launcher: the current user's registry on Windows,
/// a desktop entry on Linux, and the application bundle on macOS. It names
/// [executable], by default the running launcher.
///
/// A launcher on a data root other than the default one leaves the system
/// registration alone. The system starts the handler with the opener's
/// environment, so a link would reach the default data root rather than this
/// one; and test, acceptance and release runs, which use their own data
/// roots, must never take the player's registration.
SessionLinkRegistrar platformSessionLinkRegistrar({
  String? executable,
  String? dataRoot,
}) {
  if (Platform.isMacOS) return const BundleSessionLinkRegistrar();
  if (dataRoot != null && !isDefaultTopiaForgeDataRoot(dataRoot)) {
    return const OwnDataRootSessionLinkRegistrar();
  }
  final launcher = executable ?? Platform.resolvedExecutable;
  if (Platform.isWindows) {
    return WindowsSessionLinkRegistrar(executable: launcher);
  }
  if (Platform.isLinux) return LinuxSessionLinkRegistrar(executable: launcher);
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

/// A launcher started with its own `TOPIAFORGE_DATA_ROOT`. It neither writes
/// nor removes the system registration.
final class OwnDataRootSessionLinkRegistrar implements SessionLinkRegistrar {
  const OwnDataRootSessionLinkRegistrar();

  static const _result = SessionLinkRegistrationResult(
    SessionLinkRegistration.unsupported,
    detail:
        'This launcher runs on its own data root, so it leaves the '
        'topiaforge:// registration alone: the system would open links with '
        'the default data root.',
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
