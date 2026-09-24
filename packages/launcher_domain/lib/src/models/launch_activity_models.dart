part of '../models.dart';

/// Identity verified by the data layer, used for request-owned process actions.
final class LaunchProcessIdentity {
  const LaunchProcessIdentity({
    required this.pid,
    required this.startTimeUtc,
    required this.executablePath,
    this.nativeStartToken = '',
  });
  final int pid;
  final DateTime startTimeUtc;
  final String executablePath;
  final String nativeStartToken;
}

/// Local correlation stamps bind strict runtime records to the launch receipt.
final class LaunchActivity {
  LaunchActivity({
    required this.requestId,
    required this.profileId,
    required this.profileRevision,
    required this.installIdentity,
    required this.packageDigest,
    required this.command,
    this.process,
    this.progress,
    this.launchOutcome,
    this.sessionOutcome,
    this.unconfirmed = false,
    this.processExited = false,
  }) {
    if (command != 'main-menu' && command != 'launch-target') {
      throw const FormatException('Invalid activity command.');
    }
    if ((progress != null && progress!.requestId != requestId) ||
        (launchOutcome != null &&
            (launchOutcome!.requestId != requestId ||
                launchOutcome!.kind != 'launch' ||
                launchOutcome!.command != command)) ||
        (sessionOutcome != null &&
            (sessionOutcome!.requestId != requestId ||
                sessionOutcome!.kind != 'session'))) {
      throw const FormatException('Activity record correlation mismatch.');
    }
    final sessions = {
      if (progress?.sessionId != null) progress!.sessionId,
      if (launchOutcome?.sessionId != null) launchOutcome!.sessionId,
      if (sessionOutcome?.sessionId != null) sessionOutcome!.sessionId,
    };
    if (sessions.length > 1) {
      throw const FormatException('Activity session correlation mismatch.');
    }
  }
  final String requestId;
  final String profileId;
  final int profileRevision;
  final String installIdentity;
  final String packageDigest;
  final String command;
  final LaunchProcessIdentity? process;
  final LaunchProgress? progress;
  final LaunchOutcome? launchOutcome;
  final LaunchOutcome? sessionOutcome;
  final bool unconfirmed;

  /// Verified local exit; it does not establish a terminal runtime outcome.
  final bool processExited;
  bool get acknowledged => launchOutcome != null;
  bool get sessionStarted =>
      command == 'launch-target' && launchOutcome?.status == 'succeeded';
  int get sequence => [
    progress?.sequence ?? -1,
    launchOutcome?.sequence ?? -1,
    sessionOutcome?.sequence ?? -1,
  ].reduce((a, b) => a > b ? a : b);

  LaunchActivity copyWith({
    LaunchProcessIdentity? process,
    LaunchProgress? progress,
    LaunchOutcome? launchOutcome,
    LaunchOutcome? sessionOutcome,
    bool? unconfirmed,
    bool? processExited,
  }) => LaunchActivity(
    requestId: requestId,
    profileId: profileId,
    profileRevision: profileRevision,
    installIdentity: installIdentity,
    packageDigest: packageDigest,
    command: command,
    process: process ?? this.process,
    progress: progress ?? this.progress,
    launchOutcome: launchOutcome ?? this.launchOutcome,
    sessionOutcome: sessionOutcome ?? this.sessionOutcome,
    unconfirmed: unconfirmed ?? this.unconfirmed,
    processExited: this.processExited || (processExited ?? false),
  );

  /// Foreign, stale and post-terminal progress never changes the current view.
  LaunchActivity applyProgress(LaunchProgress next) {
    final sessionId = progress?.sessionId ?? launchOutcome?.sessionId;
    if ((sessionId != null &&
            next.sessionId != null &&
            next.sessionId != sessionId) ||
        (launchOutcome != null && launchOutcome!.status != 'succeeded')) {
      return this;
    }
    if (next.requestId != requestId ||
        next.sequence <= sequence ||
        sessionOutcome != null) {
      return this;
    }
    try {
      return copyWith(progress: next);
    } on FormatException {
      return this;
    }
  }

  /// Each outcome channel is monotonic. A terminal record may arrive first.
  LaunchActivity applyOutcome(LaunchOutcome next) {
    if (next.requestId != requestId) return this;
    final previous = next.kind == 'launch' ? launchOutcome : sessionOutcome;
    if (previous != null) return this;
    try {
      return next.kind == 'launch'
          ? copyWith(launchOutcome: next, unconfirmed: false)
          : copyWith(sessionOutcome: next);
    } on FormatException {
      return this;
    }
  }
}
