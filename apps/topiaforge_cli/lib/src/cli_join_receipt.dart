import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';

/// Bounded early records, then monotonic updates for exactly the returned receipt.
final class CliJoinReceipt {
  CliJoinReceipt(this.intent, this.profileId);
  final String intent, profileId;
  final _early = <LaunchActivity>[];
  LaunchActivity? current;
  Completer<void> _changed = Completer<void>();
  Future<void> get changed => _changed.future;

  bool bind(JoinLaunchStarted launch) {
    final receipt = launch.activity;
    if (receipt == null ||
        receipt.process == null ||
        receipt.requestId != launch.requestId ||
        receipt.profileId != profileId ||
        receipt.command != 'join-session') {
      return false;
    }
    current = LaunchActivity(
      requestId: receipt.requestId,
      profileId: receipt.profileId,
      profileRevision: receipt.profileRevision,
      installIdentity: receipt.installIdentity,
      packageDigest: receipt.packageDigest,
      command: receipt.command,
      process: receipt.process,
    );
    accept(receipt);
    for (final activity in _early) {
      accept(activity);
    }
    _early.clear();
    return true;
  }

  void accept(LaunchActivity next) {
    final previous = current;
    if (previous == null) {
      if (_early.length == 64) _early.removeAt(0);
      _early.add(next);
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
    final expected = previous.process!;
    final actual = next.process;
    if (actual == null ||
        expected.pid != actual.pid ||
        expected.startTimeUtc != actual.startTimeUtc ||
        expected.executablePath != actual.executablePath ||
        expected.nativeStartToken != actual.nativeStartToken) {
      return;
    }
    var accepted = previous.copyWith(processExited: next.processExited);
    final progress = next.progress;
    if (progress?.session?.intentId == intent &&
        progress?.session?.role == LaunchSessionRole.join) {
      accepted = accepted.applyProgress(progress!);
    }
    for (final outcome in [next.launchOutcome, next.sessionOutcome]) {
      if (outcome?.multiplayer?.intentId == intent &&
          outcome?.multiplayer?.role == LaunchSessionRole.join) {
        accepted = accepted.applyOutcome(outcome!);
      }
    }
    current = accepted;
    final waiting = _changed;
    _changed = Completer<void>();
    if (!waiting.isCompleted) waiting.complete();
  }
}
