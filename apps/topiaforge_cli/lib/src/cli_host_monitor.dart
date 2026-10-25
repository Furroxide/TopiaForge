import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';

/// Follows only the receipt returned by this host launch. Progress may precede
/// the spawn response, and an Internet invite may follow a successful outcome.
final class CliHostMonitor {
  CliHostMonitor(this.profile, this.intent, this.needsInvite, this.write);

  final LauncherProfile profile;
  final String intent;
  final bool needsInvite;
  final void Function(String) write;
  final completed = Completer<int>();
  final _pending = <LaunchActivity>[];
  LaunchActivity? _current;
  bool _closed = false, _unavailable = false;

  void bind(LaunchResult result) {
    final receipt = result.latestActivity;
    if (receipt == null ||
        receipt.requestId != result.requestId ||
        receipt.profileId != profile.id ||
        receipt.profileRevision != profile.revision ||
        receipt.command != 'host-session') {
      unavailable(force: true);
      return;
    }
    _current = receipt;
    _report();
    for (final item in _pending) {
      accept(item);
    }
    _pending.clear();
    if (_unavailable) unavailable(force: true);
  }

  void accept(LaunchActivity next) {
    if (_closed || completed.isCompleted) return;
    final previous = _current;
    if (previous == null) {
      if (_pending.length == 64) _pending.removeAt(0);
      _pending.add(next);
      return;
    }
    if (next.requestId != previous.requestId ||
        next.profileId != previous.profileId ||
        next.profileRevision != previous.profileRevision ||
        next.installIdentity != previous.installIdentity ||
        next.packageDigest != previous.packageDigest ||
        next.command != previous.command) {
      return;
    }
    final expected = previous.process;
    final actual = next.process;
    if (expected != null &&
        (actual == null ||
            expected.pid != actual.pid ||
            expected.startTimeUtc != actual.startTimeUtc ||
            expected.executablePath != actual.executablePath ||
            expected.nativeStartToken != actual.nativeStartToken)) {
      return;
    }
    var accepted = previous.copyWith(processExited: next.processExited);
    final progress = next.progress;
    if (progress?.session?.intentId == intent &&
        progress?.session?.role == LaunchSessionRole.host) {
      accepted = accepted.applyProgress(progress!);
    }
    for (final outcome in [next.launchOutcome, next.sessionOutcome]) {
      if (outcome?.multiplayer?.intentId == intent &&
          outcome?.multiplayer?.role == LaunchSessionRole.host) {
        accepted = accepted.applyOutcome(outcome!);
      }
    }
    _current = accepted;
    _report();
  }

  void _report() {
    if (completed.isCompleted || _closed) return;
    final activity = _current!;
    final outcome = activity.launchOutcome;
    if (activity.processExited || activity.sessionOutcome != null) {
      fail('Hosting ended before confirmation. Start a new hosted session.');
      return;
    }
    if (outcome?.multiplayer?.intentId != intent ||
        outcome?.multiplayer?.role != LaunchSessionRole.host) {
      return;
    }
    if (outcome!.status != 'succeeded') {
      final message = switch (outcome.multiplayer?.failure) {
        LaunchMultiplayerFailure.portInUse =>
          'The hosting port is in use. Choose another port with --port.',
        LaunchMultiplayerFailure.identityUnavailable =>
          'The multiplayer identity is unavailable. Close programs using the game data and retry.',
        LaunchMultiplayerFailure.networkStopped =>
          'The hosting network stopped. Check your connection and retry.',
        LaunchMultiplayerFailure.serviceUnavailable =>
          'The multiplayer service could not register this host. Retry later or use --visibility lan.',
        _ =>
          'The game refused hosting. Check the in-game status and repair the selected profile before retrying.',
      };
      fail(message);
      return;
    }
    final session = activity.progress?.session;
    if (session?.intentId != intent ||
        session?.role != LaunchSessionRole.host ||
        session?.state != LaunchSessionState.hosting) {
      return;
    }
    final code = session!.inviteCode;
    if (needsInvite && code == null) return;
    write('TFHOST140: Hosting confirmed.');
    if (code != null) {
      write('Invite code: $code');
      write('Invite link: topiaforge://join/$code');
    } else {
      write(
        'This visibility has no Internet invite; players can use the LAN browser or an explicitly shared target.',
      );
    }
    completed.complete(0);
  }

  void fail(String message) {
    if (_closed || completed.isCompleted) return;
    write('TFHOST140: $message');
    completed.complete(1);
  }

  void unavailable({bool force = false}) {
    if (_closed || completed.isCompleted) return;
    if (_current == null && !force) {
      _unavailable = true;
      return;
    }
    write(
      'TFHOST140: Hosting or its invite is unconfirmed. Check the game before retrying; it was left running.',
    );
    completed.complete(3);
  }

  void cancel() {
    if (_closed || completed.isCompleted) return;
    write(
      'TFHOST140: Stopped waiting. Check the game for its hosting status; it was left unchanged.',
    );
    completed.complete(130);
  }

  void close() {
    _closed = true;
    _pending.clear();
  }
}
