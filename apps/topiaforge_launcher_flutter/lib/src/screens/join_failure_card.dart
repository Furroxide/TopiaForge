part of '../screens.dart';

/// The outcome of a join that stopped: what happened, in one clear sentence,
/// what to do next, Retry only where trying again can help, the step it
/// stopped at, and Copy details, which holds no address.
class _JoinFailureView extends StatelessWidget {
  const _JoinFailureView({required this.flow, required this.failure});

  final JoinFlow flow;
  final JoinFailure failure;

  /// Whether the game already had the join when it stopped, so it may still
  /// go on there.
  bool get _startedGame =>
      flow.launch != null && failure.stage.index >= JoinStage.launching.index;

  @override
  Widget build(BuildContext context) {
    final text = JoinFailureText.of(failure, startedGame: _startedGame);
    final cancelled = failure.code == JoinFailureCode.cancelled;
    final theme = Theme.of(context);
    // A blocked mod's id is the host's own word, as its message is.
    final blockers = [
      for (final blocker in failure.blockers)
        blocker.modId.isEmpty
            ? withoutAddresses(blocker.message)
            : '${withoutAddresses(blocker.modId)}: '
                  '${withoutAddresses(blocker.message)}',
    ];
    return _JoinFrame(
      key: const Key('join-failure-card'),
      title: text.title,
      icon: cancelled ? Icons.cancel_outlined : Icons.error_outline,
      tone: cancelled ? TopiaForgePalette.mutedText : TopiaForgePalette.danger,
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          // A plan's failure names its first blocker; the list says them
          // all, so the body is the general sentence.
          Text(
            blockers.isEmpty ? text.bodyFor(failure) : text.sentence,
            key: const Key('join-failure-body'),
          ),
          if (text.nextStep case final next?) ...[
            const SizedBox(height: 8),
            Text(
              'What to do: $next',
              key: const Key('join-failure-next'),
              style: theme.textTheme.titleSmall,
            ),
          ],
          if (blockers.isNotEmpty) ...[
            const SizedBox(height: 10),
            for (final line in blockers)
              Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Padding(
                      padding: EdgeInsets.only(top: 2, right: 8),
                      child: Icon(
                        Icons.extension_off_outlined,
                        size: 16,
                        color: TopiaForgePalette.danger,
                      ),
                    ),
                    Expanded(child: Text(line)),
                  ],
                ),
              ),
          ],
          const SizedBox(height: 14),
          Text('Where it stopped', style: theme.textTheme.titleSmall),
          const SizedBox(height: 6),
          StepRail(steps: joinRailSteps(flow)),
        ],
      ),
      actions: [
        _CopyDetailsButton(details: joinFailureDetails(flow, failure, text)),
        TextButton(
          key: const Key('join-close'),
          autofocus: !text.retry && text.action == JoinFailureAction.none,
          onPressed: () => _add(context, MultiplayerJoinClosed(flow.id)),
          child: const Text('Close'),
        ),
        ...switch (text.action) {
          JoinFailureAction.allowDirect => [
            OutlinedButton.icon(
              key: const Key('join-allow-direct'),
              autofocus: !text.retry,
              onPressed: () =>
                  _add(context, MultiplayerJoinDirectAsked(flow.id)),
              icon: const Icon(Icons.public),
              label: const Text('Allow a direct connection…'),
            ),
          ],
          JoinFailureAction.trust when failure.servedServerId != null => [
            OutlinedButton.icon(
              key: const Key('join-trust'),
              autofocus: !text.retry,
              onPressed: () => _add(context, MultiplayerJoinTrusted(flow.id)),
              icon: const Icon(Icons.verified_user_outlined),
              label: const Text('Trust this server and join'),
            ),
          ],
          JoinFailureAction.repair => [
            OutlinedButton.icon(
              key: const Key('join-repair'),
              onPressed: () {
                _add(context, const RuntimeRepaired());
                _add(context, MultiplayerJoinRetried(flow.id));
              },
              icon: const Icon(Icons.build_outlined),
              label: const Text('Repair, then try again'),
            ),
          ],
          _ => const <Widget>[],
        },
        if (text.retry)
          FilledButton.icon(
            key: const Key('join-retry'),
            autofocus: true,
            onPressed: () => _add(context, MultiplayerJoinRetried(flow.id)),
            icon: const Icon(Icons.refresh),
            label: Text(cancelled ? 'Join again' : 'Try again'),
          ),
      ],
    );
  }
}

/// What Copy details copies: the step, the reason and the launcher's words,
/// without any address: no target, nothing typed, and the game's and the
/// host's words with addresses taken out.
String joinFailureDetails(
  JoinFlow flow,
  JoinFailure failure,
  JoinFailureText text,
) {
  final step = joinSteps
      .firstWhere(
        (entry) => entry.$1 == failure.stage,
        orElse: () => (failure.stage, failure.stage.name),
      )
      .$2;
  return [
    'TopiaForge join',
    'Server: ${flow.serverName ?? 'not known yet'}',
    'Stopped at: $step',
    'Reason: ${failure.code.name}',
    text.title,
    text.bodyFor(failure),
    if (text.nextStep case final next?) 'What to do: $next',
    for (final blocker in failure.blockers)
      'Mod ${blocker.modId.isEmpty ? '(session)' : withoutAddresses(blocker.modId)}: '
          '${withoutAddresses(blocker.message)}',
    'Privacy: ${flow.privacy.wireName}',
    'TopiaForge ${TopiaForgeRuntimeVersions.loaderVersion}',
  ].join('\n');
}

class _CopyDetailsButton extends StatefulWidget {
  const _CopyDetailsButton({required this.details});

  final String details;

  @override
  State<_CopyDetailsButton> createState() => _CopyDetailsButtonState();
}

class _CopyDetailsButtonState extends State<_CopyDetailsButton> {
  var _copied = false;

  @override
  Widget build(BuildContext context) => TextButton.icon(
    key: const Key('join-copy-details'),
    onPressed: () async {
      await Clipboard.setData(ClipboardData(text: widget.details));
      if (mounted) setState(() => _copied = true);
    },
    icon: Icon(_copied ? Icons.check : Icons.copy_all_outlined),
    label: Text(_copied ? 'Copied' : 'Copy details'),
  );
}
