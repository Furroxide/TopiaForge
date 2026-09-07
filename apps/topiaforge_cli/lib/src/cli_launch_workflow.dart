import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';
import 'cli_launch_options.dart';

/// Exit 0 confirms the requested start; 3 means no runtime confirmation.
/// Explicit --no-wait returns process-start status and labels it unconfirmed.
Future<int> runCliLaunch({
  required LauncherRepository repository,
  required GameInstall install,
  required LauncherProfile profile,
  required CliLaunchOptions options,
  required void Function(String) write,
  bool restart = false,
  Future<void>? cancelled,
}) async {
  final monitor = _LaunchMonitor(profile, write);
  if (cancelled != null) {
    unawaited(
      cancelled.then(
        (_) => monitor.cancel(),
        onError: (Object _) => monitor.cancel(),
      ),
    );
  }
  final subscription = repository.launchActivities.listen(
    monitor.accept,
    onError: (Object error, StackTrace stack) => monitor.unavailable(),
    onDone: monitor.unavailable,
  );
  Timer? timeout;
  try {
    final preview = await repository.previewLaunch(
      install,
      profile,
      selectionOverride: options.selectionOverride,
    );
    if (monitor.completed.isCompleted) return await monitor.completed.future;
    if (preview.profileId != profile.id ||
        preview.profileRevision != profile.revision) {
      write('Launch options changed; refresh this profile and try again.');
      return 1;
    }
    if (!preview.canLaunch) {
      write(
        'Launch is blocked. ${[...preview.issues.where((issue) => issue.isBlocking).map((issue) => issue.message), ...preview.blocks.map((block) => block.message), if (preview.effectiveSelection.kind == LaunchSelectionKind.unresolvedLegacy) 'Repair the unavailable saved selection with --target <id> or --main-menu.'].join(' ')}',
      );
      return 1;
    }
    final result = restart
        ? await repository.restart(
            install,
            profile,
            selectionOverride: options.selectionOverride,
          )
        : await repository.launch(
            install,
            profile,
            selectionOverride: options.selectionOverride,
          );
    if (!result.processStarted) {
      write(
        [
          result.message,
          ...result.issues.map((issue) => issue.message),
          ...result.blocks.map((block) => block.message),
        ].join(' '),
      );
      return 1;
    }
    write(
      'Game process started${result.requestId == null ? '' : ' (request ${result.requestId})'}; session start is unconfirmed.',
    );
    monitor.bind(result);
    if (!options.waitForAcknowledgement) return 0;
    if (result.requestId == null || monitor.current == null) {
      write(
        'Runtime acknowledgement is unavailable; session start is unconfirmed.',
      );
      return 3;
    }
    timeout = Timer(options.waitTimeout, monitor.unavailable);
    return await monitor.completed.future;
  } finally {
    timeout?.cancel();
    monitor.close();
    await subscription.cancel();
  }
}

final class _LaunchMonitor {
  _LaunchMonitor(this.profile, this.write);
  final LauncherProfile profile;
  final void Function(String) write;
  final completed = Completer<int>();
  final pending = <LaunchActivity>[];
  LaunchActivity? current;
  bool closed = false;
  bool unavailableBeforeReceipt = false;

  void bind(LaunchResult result) {
    final receipt = result.latestActivity;
    if (receipt == null ||
        receipt.requestId != result.requestId ||
        receipt.profileId != profile.id ||
        receipt.profileRevision != profile.revision) {
      unavailable();
      return;
    }
    current = receipt;
    _report();
    for (final activity in pending) {
      accept(activity);
    }
    pending.clear();
    if (unavailableBeforeReceipt) unavailable();
  }

  void accept(LaunchActivity next) {
    if (closed || completed.isCompleted) return;
    final previous = current;
    if (previous == null) {
      if (pending.length == 64) pending.removeAt(0);
      pending.add(next);
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
    final expectedProcess = previous.process;
    final actualProcess = next.process;
    if (expectedProcess != null &&
        actualProcess != null &&
        (expectedProcess.pid != actualProcess.pid ||
            expectedProcess.startTimeUtc != actualProcess.startTimeUtc ||
            expectedProcess.executablePath != actualProcess.executablePath ||
            expectedProcess.nativeStartToken !=
                actualProcess.nativeStartToken)) {
      return;
    }
    var accepted = previous.copyWith(processExited: next.processExited);
    if (next.progress != null) {
      accepted = accepted.applyProgress(next.progress!);
    }
    if (next.launchOutcome != null) {
      accepted = accepted.applyOutcome(next.launchOutcome!);
    }
    if (next.sessionOutcome != null) {
      accepted = accepted.applyOutcome(next.sessionOutcome!);
    }
    current = accepted;
    if (accepted.progress != previous.progress) {
      write('Runtime: ${accepted.progress!.phase}.');
    }
    _report();
  }

  void _report() {
    if (completed.isCompleted) return;
    final activity = current!;
    final outcome = activity.launchOutcome;
    if (outcome != null) {
      if (outcome.status == 'succeeded') {
        write(
          outcome.command == 'main-menu'
              ? 'Main menu confirmed.'
              : 'Session started; Running confirmed.',
        );
        completed.complete(0);
      } else {
        write(
          [
            'Session startup ${outcome.status}.',
            ...outcome.blocks.map((block) => block.message),
            if (outcome.error != null) outcome.error!.message,
          ].join(' '),
        );
        completed.complete(1);
      }
    } else if (activity.processExited) {
      unavailable();
    }
  }

  void unavailable() {
    if (closed || completed.isCompleted) return;
    if (current == null) {
      unavailableBeforeReceipt = true;
      return;
    }
    write(
      'Runtime acknowledgement is unavailable; session start is unconfirmed.',
    );
    completed.complete(3);
  }

  void cancel() {
    if (closed || completed.isCompleted) return;
    write(
      'Stopped waiting; the game process is unchanged and session status is unconfirmed.',
    );
    completed.complete(130);
  }

  void close() {
    closed = true;
    pending.clear();
  }
}
