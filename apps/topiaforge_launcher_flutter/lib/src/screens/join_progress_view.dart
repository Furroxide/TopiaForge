part of '../screens.dart';

/// A running join: its steps, the one it is on and how far that is, and
/// Cancel, which works at every step. A step that must finish before it can
/// stop says so.
class _JoinProgressView extends StatelessWidget {
  const _JoinProgressView({required this.flow});

  final JoinFlow flow;

  @override
  Widget build(BuildContext context) {
    final join = flow.join!;
    final name = flow.serverName;
    return _JoinFrame(
      key: const Key('join-progress'),
      title: name == null ? 'Joining a server' : 'Joining $name',
      icon: Icons.groups,
      tone: TopiaForgePalette.launchDark,
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          if (join.cancelRequested) ...[
            const SessionNotice(
              key: Key('join-stopping'),
              message:
                  'This step finishes before the join can stop, so nothing is '
                  'left half done. The join stops as soon as it has.',
            ),
            const SizedBox(height: 12),
          ],
          StepRail(steps: joinRailSteps(flow)),
        ],
      ),
      actions: [_cancelButton(context, flow)],
    );
  }
}

/// The rail's steps for [flow]: done, running, waiting, not needed, or
/// where its join stopped.
List<StepRailItem> joinRailSteps(JoinFlow flow) {
  final join = flow.join!;
  final failure = join.outcome?.failure;
  final at = failure?.stage ?? join.stage;
  final plan = join.plan ?? flow.plan;
  bool skipped(JoinStage stage) => switch (stage) {
    JoinStage.resolving => !join.target.resolvesThroughService,
    JoinStage.downloading ||
    // A blocked plan downloads nothing yet; once fixed, it may.
    JoinStage.installing =>
      plan != null && plan.isJoinable && !plan.needsDownloads,
    _ => false,
  };
  return [
    for (final (stage, label) in joinSteps)
      StepRailItem(
        label: label,
        status: stage == at
            ? (failure == null ? StepRailStatus.current : StepRailStatus.failed)
            : skipped(stage)
            ? StepRailStatus.skipped
            : stage.index < at.index
            ? StepRailStatus.done
            : StepRailStatus.pending,
        detail: stage == at && failure == null ? _stepDetail(flow) : null,
        progress: stage == at && failure == null ? _stepProgress(join) : null,
      ),
  ];
}

String? _stepDetail(JoinFlow flow) {
  final join = flow.join!;
  return switch (join.stage) {
    JoinStage.resolving => 'Asking the TopiaForge service where it is.',
    JoinStage.fetchingDescriptor =>
      join.target.resolvesThroughService
          ? 'Verifying the server through the TopiaForge service.'
          : 'Asking the server what it runs, and verifying the answer.',
    JoinStage.checking =>
      'Matching the server to your installed mods and your mod sources.',
    JoinStage.downloading => switch (join.progress) {
      JoinTransferProgress(:final bytesDone, :final bytesTotal)
          when bytesTotal > 0 =>
        '${_formatBytes(bytesDone)} of ${_formatBytes(bytesTotal)}',
      _ => 'Starting the downloads.',
    },
    JoinStage.installing => switch (join.installProgress) {
      JoinInstallProgress(:final packagesDone, :final packagesTotal) =>
        '$packagesDone of $packagesTotal ${packagesTotal == 1 ? 'mod' : 'mods'} '
            'ready to install.',
      _ => 'Installing as one change, so it is undone whole if anything fails.',
    },
    JoinStage.preparingProfile =>
      "Saving the server's profile, with every mod pinned.",
    JoinStage.launching =>
      flow.waitingLong
          ? 'Robotopia has not answered yet. It may still be loading; you can '
                'wait, or cancel.'
          : flow.launch?.live ?? false
          ? 'Robotopia already runs these mods, so it joins without '
                'restarting.'
          : 'Writing your settings for the game, then starting Robotopia.',
    JoinStage.connecting =>
      'Robotopia is connecting. If the server has a password, type it there.',
    JoinStage.synchronizing => "Robotopia is receiving the server's world.",
    _ => null,
  };
}

double? _stepProgress(JoinSessionState join) => switch (join.stage) {
  JoinStage.downloading => switch (join.progress) {
    JoinTransferProgress(:final bytesTotal, :final fraction)
        when bytesTotal > 0 =>
      fraction,
    _ => null,
  },
  JoinStage.installing => switch (join.installProgress) {
    JoinInstallProgress(:final packagesTotal, :final fraction)
        when packagesTotal > 0 =>
      fraction,
    _ => null,
  },
  _ => null,
};
