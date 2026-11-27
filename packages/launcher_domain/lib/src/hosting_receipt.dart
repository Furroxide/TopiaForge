import 'launch_resolution.dart';
import 'models.dart';

/// Correlates host records with the exact process receipt, profile and intent.
/// Events arriving during spawn are buffered; no invite is exposed until both
/// a successful outcome and hosting progress agree with the receipt.
final class HostingReceipt {
  HostingReceipt(this.profile, this.intentId, {required this.needsInvite});
  final LauncherProfile profile;
  final String intentId;
  final bool needsInvite;
  final _pending = <LaunchActivity>[];
  LaunchActivity? _activity;
  bool _bound = false;
  bool _invalid = false;

  LaunchActivity? get activity => _activity;
  bool get ended =>
      _activity?.processExited == true || _activity?.sessionOutcome != null;
  bool get failed =>
      _invalid ||
      ended ||
      (_activity?.launchOutcome != null &&
          _activity?.launchOutcome?.status != 'succeeded');
  bool get confirmed =>
      !failed &&
      _activity?.launchOutcome?.status == 'succeeded' &&
      _activity?.progress?.session?.state == LaunchSessionState.hosting &&
      (!needsInvite || _activity?.progress?.session?.inviteCode != null);
  String? get inviteCode =>
      confirmed ? _activity?.progress?.session?.inviteCode : null;

  void bind(LaunchResult result) {
    if (_bound) return;
    _bound = true;
    final receipt = result.latestActivity;
    if (!result.processStarted ||
        receipt == null ||
        receipt.requestId != result.requestId ||
        receipt.profileId != profile.id ||
        receipt.profileRevision != profile.revision ||
        receipt.command != 'host-session' ||
        receipt.process == null) {
      _invalid = true;
      _pending.clear();
      return;
    }
    // Even the first receipt must filter its embedded session data.
    _activity = LaunchActivity(
      requestId: receipt.requestId,
      profileId: receipt.profileId,
      profileRevision: receipt.profileRevision,
      installIdentity: receipt.installIdentity,
      packageDigest: receipt.packageDigest,
      command: receipt.command,
      process: receipt.process,
    );
    accept(receipt);
    for (final item in _pending) {
      accept(item);
    }
    _pending.clear();
  }

  void accept(LaunchActivity next) {
    if (_invalid || ended) return;
    final previous = _activity;
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
    if (progress?.session?.intentId == intentId &&
        progress?.session?.role == LaunchSessionRole.host) {
      accepted = accepted.applyProgress(progress!);
    }
    for (final outcome in [next.launchOutcome, next.sessionOutcome]) {
      if (outcome?.multiplayer?.intentId == intentId &&
          outcome?.multiplayer?.role == LaunchSessionRole.host) {
        accepted = accepted.applyOutcome(outcome!);
      }
    }
    _activity = accepted;
  }
}
