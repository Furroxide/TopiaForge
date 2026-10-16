import 'dart:async';
import 'dart:io';

import 'launcher_instance.dart';
import 'update_health_handshake.dart';

/// What a start of the launcher is for, read from its command line before
/// the launcher claims anything.
final class LauncherStart {
  const LauncherStart._({this.link, this.healthCheck});

  /// Reads [arguments]. A link start ignores every argument after its link.
  /// Any other start that names a health option must be the update helper's
  /// exact health check, or this throws [StateError].
  factory LauncherStart.read(
    List<String> arguments, {
    required String dataRoot,
  }) {
    final link = sessionLinkArgument(arguments);
    if (link != null) return LauncherStart._(link: link);
    return LauncherStart._(
      healthCheck: readUpdateHealthRequest(arguments, dataRoot: dataRoot),
    );
  }

  /// The session link of a link start.
  final String? link;

  /// The update helper's health check.
  final UpdateHealthRequest? healthCheck;
}

/// How [LauncherStartup.run] ended.
enum LauncherStartOutcome {
  /// The launcher started.
  started,

  /// The launcher that owns the data root took the link, and this one quit.
  handedOff,

  /// The command line was a broken health check. Nothing was claimed, and
  /// the launcher quit.
  refused,

  /// Starting failed after the claim. The claim was released, and the
  /// launcher quit.
  failed,
}

/// The way from `main` to the first frame.
///
/// Nothing may hold the launcher's data root without a window, or every
/// later start would hand its link to it and nothing would ever open. So a
/// broken health check quits before it claims anything, and a start that
/// fails after its claim releases it and quits.
final class LauncherStartup {
  const LauncherStartup({
    required LauncherInstancePort instance,
    required Future<void> Function(int exitCode) exit,
  }) : _instance = instance,
       _exit = exit;

  final LauncherInstancePort _instance;
  final Future<void> Function(int exitCode) _exit;

  Future<LauncherStartOutcome> run(
    List<String> arguments, {
    required String dataRoot,
    required String scope,
    required FutureOr<void> Function(LauncherStart start) launch,
  }) async {
    final LauncherStart start;
    try {
      start = LauncherStart.read(arguments, dataRoot: dataRoot);
    } on Object catch (error) {
      _report(error);
      await _exit(1);
      return LauncherStartOutcome.refused;
    }
    final role = await LauncherInstanceGate(
      _instance,
    ).resolve(arguments, scope: scope);
    if (role == LauncherInstanceRole.handedOff) {
      await _exit(0);
      return LauncherStartOutcome.handedOff;
    }
    try {
      await launch(start);
      return LauncherStartOutcome.started;
    } on Object catch (error) {
      _report(error);
      try {
        await _instance.release();
      } on Object {
        // Quitting releases the claim as well.
      }
      await _exit(1);
      return LauncherStartOutcome.failed;
    }
  }

  // None of these errors holds a link: before the first frame a link is only
  // passed along, never read.
  static void _report(Object error) =>
      stderr.writeln('TopiaForge could not start: $error');
}
