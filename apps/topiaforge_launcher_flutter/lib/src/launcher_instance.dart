import 'package:launcher_domain/launcher_domain.dart';

import 'update_health_handshake.dart';

/// Who owns a launcher scope, as the runner sees it.
enum LauncherInstanceClaim {
  /// This process claimed the scope, and links handed over arrive here.
  primary,

  /// Another running launcher owns the scope.
  secondary,

  /// The runner keeps no claim of its own, because the system already keeps
  /// the launcher single (macOS LaunchServices, Linux D-Bus), or the claim
  /// could not be made.
  notSupported,
}

/// What this process does at start.
enum LauncherInstanceRole {
  /// It owns its scope, or the system keeps it single: it starts normally.
  primary,

  /// Another launcher owns the scope and this one starts beside it without
  /// taking its links: an update health check, or an owner that did not
  /// answer.
  standalone,

  /// It handed its link to the owner, which comes forward, and exits without
  /// starting.
  handedOff,
}

/// The runner's single-instance calls (`windows/runner/single_instance.cpp`).
abstract interface class LauncherInstancePort {
  /// Claims [scope] for this process.
  Future<LauncherInstanceClaim> claim(String scope);

  /// Hands [link] to the owner of the scope, or with null only asks it to
  /// come forward. True when the owner took it.
  Future<bool> handOff(String? link);

  /// Gives up the scope this process claimed, so a later start can claim
  /// it. Nothing happens when this process holds none.
  Future<void> release();
}

/// The launcher's start rule: one launcher per user and data root.
///
/// A later start hands its link, if it has one, to the launcher that owns
/// the scope, and exits. An update health check never hands off and never
/// exits for another launcher: the update helper waits for its health
/// marker and rolls the update back without it. When another launcher
/// still owns the scope, the health check runs beside it, and that
/// launcher keeps receiving links.
final class LauncherInstanceGate {
  const LauncherInstanceGate(this._port);

  final LauncherInstancePort _port;

  Future<LauncherInstanceRole> resolve(
    List<String> arguments, {
    required String scope,
  }) async {
    try {
      final claim = await _port.claim(scope);
      if (claim != LauncherInstanceClaim.secondary) {
        return LauncherInstanceRole.primary;
      }
      if (isUpdateHealthStart(arguments)) {
        return LauncherInstanceRole.standalone;
      }
      if (await _port.handOff(sessionLinkArgument(arguments))) {
        return LauncherInstanceRole.handedOff;
      }
      // The owner did not answer. It may just have exited, so claim once
      // more before starting beside it.
      return await _port.claim(scope) == LauncherInstanceClaim.primary
          ? LauncherInstanceRole.primary
          : LauncherInstanceRole.standalone;
    } on Object {
      // The runner failed: start as the launcher always has.
      return LauncherInstanceRole.primary;
    }
  }
}

/// The session link of a link start, or null for any other start.
///
/// A start is a link start when its first argument has the `topiaforge:`
/// scheme, and then that argument is the link and every other argument is
/// ignored. A quote inside a link can split it into more arguments, so
/// nothing after the first may count. On Windows the runner goes further and
/// passes the rest of the raw command line as the one argument, so a split
/// link reads as the damaged link it is.
String? sessionLinkArgument(List<String> arguments) {
  if (arguments.isEmpty || !SessionLink.hasScheme(arguments.first)) {
    return null;
  }
  return arguments.first;
}
