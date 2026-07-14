import 'dart:io';

/// Best-effort process liveness check for the PID recorded in a publisher
/// session lease. A stale lease must never be treated as a running publisher.
class UgcPublisherLiveness {
  const UgcPublisherLiveness._();

  static bool isAlive(int pid) {
    if (pid <= 0) {
      return false;
    }
    try {
      if (Platform.isWindows) {
        return _isAliveOnWindows(pid);
      }
      final kill = File('/bin/kill').existsSync() ? '/bin/kill' : 'kill';
      return Process.runSync(kill, ['-0', '$pid']).exitCode == 0;
    } on ProcessException {
      return false;
    } on FileSystemException {
      return false;
    }
  }

  static bool _isAliveOnWindows(int pid) {
    final result = Process.runSync('tasklist', [
      '/FI',
      'PID eq $pid',
      '/FO',
      'CSV',
      '/NH',
    ]);
    if (result.exitCode != 0) {
      return false;
    }
    final pidField = RegExp(r'^"[^"]*","([0-9]+)"');
    for (final line in result.stdout.toString().split(RegExp(r'[\r\n]+'))) {
      final match = pidField.firstMatch(line.trim());
      if (match != null && int.tryParse(match.group(1)!) == pid) {
        return true;
      }
    }
    return false;
  }
}
