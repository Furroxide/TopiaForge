part of '../local_launcher_repository.dart';

extension _NoncriticalLogging on LocalLauncherRepository {
  /// Records a noncritical diagnostic without allowing an unavailable log
  /// sink to change the result of an operation that is already durable.
  Future<void> _appendLauncherLogBestEffort(String message) async {
    try {
      await _appendLauncherLog(message);
    } on Object catch (error) {
      final diagnostic = _sanitizeLauncherLogMessage(
        'TFLOG001: Launcher file logging was unavailable after a completed '
        'operation (${error.runtimeType}: $error).',
      );
      try {
        stderr.writeln(diagnostic);
      } on Object {
        // Both diagnostics sinks are noncritical at this point. The primary
        // operation has already reached a durable state and must stay valid.
      }
    }
  }
}
