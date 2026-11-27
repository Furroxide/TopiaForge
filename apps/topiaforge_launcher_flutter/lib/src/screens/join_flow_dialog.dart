part of '../screens.dart';

/// Opens the join flow's dialog while a flow shows one, and lets it close
/// when the flow ends or the game takes the join over.
///
/// The dialog is one route for the whole flow, whose content follows the
/// bloc's state: the questions, the consent sheet, the progress and the
/// outcome. Nothing but the state opens or closes it, so it never shows a
/// flow that has moved on.
class JoinFlowHost extends StatefulWidget {
  const JoinFlowHost({super.key, required this.child});

  final Widget child;

  @override
  State<JoinFlowHost> createState() => _JoinFlowHostState();
}

class _JoinFlowHostState extends State<JoinFlowHost> {
  var _open = false;

  static bool _shows(LauncherState state) =>
      state.multiplayer.join.flow?.showsDialog ?? false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _shows(context.read<LauncherBloc>().state)) _show();
    });
  }

  Future<void> _show() async {
    if (_open) return;
    _open = true;
    final bloc = context.read<LauncherBloc>();
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) =>
          BlocProvider.value(value: bloc, child: const JoinFlowDialog()),
    );
    _open = false;
    // A newer flow that opened as the last one closed gets its own.
    if (mounted && _shows(bloc.state)) await _show();
  }

  @override
  Widget build(BuildContext context) =>
      BlocListener<LauncherBloc, LauncherState>(
        listenWhen: (previous, current) => _shows(previous) != _shows(current),
        listener: (context, state) {
          if (_shows(state)) _show();
        },
        child: widget.child,
      );
}

/// The join flow's dialog: whatever the flow shows now.
class JoinFlowDialog extends StatelessWidget {
  const JoinFlowDialog({super.key});

  @override
  Widget build(BuildContext context) {
    return BlocConsumer<LauncherBloc, LauncherState>(
      listenWhen: (previous, current) =>
          (current.multiplayer.join.flow?.showsDialog ?? false) == false,
      listener: (context, state) {
        final route = ModalRoute.of(context);
        if (route == null || !route.isActive) return;
        if (route.isCurrent) {
          Navigator.of(context).pop();
        } else {
          Navigator.of(context).removeRoute(route);
        }
      },
      buildWhen: (previous, current) =>
          current.multiplayer.join.flow?.showsDialog ?? false,
      builder: (context, state) {
        final flow = state.multiplayer.join.flow;
        if (flow == null) return const SizedBox.shrink();
        // The dialog cannot be dismissed by its barrier, so Escape is wired
        // here: it does what Cancel or Close would.
        return PopScope(
          canPop: false,
          onPopInvokedWithResult: (popped, _) {
            if (!popped) _escape(context, flow);
          },
          child: Actions(
            actions: {
              DismissIntent: CallbackAction<DismissIntent>(
                onInvoke: (_) {
                  _escape(context, flow);
                  return null;
                },
              ),
            },
            child: Dialog(
              key: const Key('join-dialog'),
              insetPadding: const EdgeInsets.symmetric(
                horizontal: 24,
                vertical: 20,
              ),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 760),
                child: _JoinFlowView(flow: flow, state: state),
              ),
            ),
          ),
        );
      },
    );
  }

  /// Escape answers a question no, stops a running join, and closes an
  /// outcome.
  static void _escape(BuildContext context, JoinFlow flow) {
    if (flow.question != null) {
      _add(context, MultiplayerJoinQuestionAnswered(flow: flow.id, yes: false));
    } else if (flow.join != null && !flow.join!.isEnded) {
      _add(context, MultiplayerJoinCancelled(flow.id));
    } else {
      _add(context, MultiplayerJoinClosed(flow.id));
    }
  }
}

class _JoinFlowView extends StatelessWidget {
  const _JoinFlowView({required this.flow, required this.state});

  final JoinFlow flow;
  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final join = flow.join;
    final failure = join?.outcome?.failure;
    return switch (flow) {
      JoinFlow(question: JoinQuestion.disclosure) => _JoinFrame(
        key: const Key('join-question-disclosure'),
        title: MultiplayerPrivacyText.disclosureTitle,
        body: const PrivacyDisclosureBody(),
        actions: [
          FilledButton(
            key: const Key('join-understand'),
            autofocus: true,
            onPressed: () => _add(
              context,
              MultiplayerJoinQuestionAnswered(flow: flow.id, yes: true),
            ),
            child: const Text(MultiplayerPrivacyText.disclosureButton),
          ),
        ],
      ),
      JoinFlow(question: JoinQuestion.directConnection) => _JoinFrame(
        key: const Key('join-question-direct'),
        title: MultiplayerPrivacyText.directTitle,
        body: const Text(MultiplayerPrivacyText.directBody),
        actions: [
          DirectConnectionActions(
            onAllow: () => _add(
              context,
              MultiplayerJoinQuestionAnswered(flow: flow.id, yes: true),
            ),
            onCancel: () => _add(
              context,
              MultiplayerJoinQuestionAnswered(flow: flow.id, yes: false),
            ),
          ),
        ],
      ),
      JoinFlow(problem: final problem?) => _JoinProblemView(
        flow: flow,
        problem: problem,
      ),
      _ when join == null => _JoinFrame(
        key: const Key('join-preparing'),
        title: 'Getting ready to join',
        body: const _Waiting(text: 'Reading your multiplayer settings…'),
        actions: [_cancelButton(context, flow)],
      ),
      _ when failure != null => _JoinFailureView(flow: flow, failure: failure),
      _ when join.stage == JoinStage.awaitingConsent => _JoinConsentSheet(
        flow: flow,
        state: state,
      ),
      _ => _JoinProgressView(flow: flow),
    };
  }
}

/// A Cancel button that stops [flow]; the focus starts on it.
Widget _cancelButton(BuildContext context, JoinFlow flow) {
  final stopping = flow.join?.cancelRequested ?? false;
  return TextButton(
    key: const Key('join-cancel'),
    autofocus: true,
    onPressed: stopping
        ? null
        : () => _add(context, MultiplayerJoinCancelled(flow.id)),
    child: Text(stopping ? 'Stopping…' : 'Cancel'),
  );
}

/// The flow dialog's frame: a title, a body that scrolls when the window is
/// short or the text large, and its buttons.
class _JoinFrame extends StatelessWidget {
  const _JoinFrame({
    super.key,
    required this.title,
    required this.body,
    required this.actions,
    this.icon,
    this.tone = TopiaForgePalette.text,
  });

  final String title;
  final Widget body;
  final List<Widget> actions;
  final IconData? icon;
  final Color tone;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 22, 24, 18),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (icon != null) ...[
                Padding(
                  padding: const EdgeInsets.only(top: 2, right: 10),
                  child: Icon(icon, color: tone, size: 24),
                ),
              ],
              Expanded(
                child: Semantics(
                  header: true,
                  child: Text(title, style: theme.dialogTheme.titleTextStyle),
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),
          Flexible(
            child: SingleChildScrollView(
              key: const Key('join-scroll'),
              child: DefaultTextStyle.merge(
                style: theme.dialogTheme.contentTextStyle,
                child: body,
              ),
            ),
          ),
          const SizedBox(height: 16),
          Wrap(
            alignment: WrapAlignment.end,
            spacing: 8,
            runSpacing: 8,
            children: actions,
          ),
        ],
      ),
    );
  }
}

class _Waiting extends StatelessWidget {
  const _Waiting({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    mainAxisSize: MainAxisSize.min,
    children: [
      Text(text),
      const SizedBox(height: 10),
      if (!MediaQuery.of(context).disableAnimations)
        const LinearProgressIndicator(minHeight: 4),
    ],
  );
}

class _JoinProblemView extends StatelessWidget {
  const _JoinProblemView({required this.flow, required this.problem});

  final JoinFlow flow;
  final JoinFlowProblem problem;

  @override
  Widget build(BuildContext context) {
    final (String title, String body) = switch (problem) {
      JoinFlowProblem.noInstall => (
        'Select Robotopia first',
        "Joining installs the server's mods into Robotopia and starts it, so "
            'the launcher needs to know where the game is. Select its install '
            'in Setup, then join again.',
      ),
      JoinFlowProblem.settingsUnreadable => (
        'Your multiplayer settings could not be read',
        'Your multiplayer settings could not be read just now, so nothing was '
            'joined. Another program may be using the file. Try again in a '
            'moment.',
      ),
      JoinFlowProblem.disclosureNotSaved => (
        'That you read the disclosure was not saved',
        'You read the address disclosure, but the launcher could not save '
            'that, so nothing was joined. Check that TopiaForge can write its '
            'data folder, then try again.',
      ),
    };
    return _JoinFrame(
      key: const Key('join-problem'),
      title: title,
      icon: Icons.warning_amber,
      tone: TopiaForgePalette.launchDark,
      body: Text(body),
      actions: [
        TextButton(
          key: const Key('join-close'),
          onPressed: () => _add(context, MultiplayerJoinClosed(flow.id)),
          child: const Text('Close'),
        ),
        if (problem == JoinFlowProblem.noInstall)
          FilledButton.icon(
            key: const Key('join-open-setup'),
            autofocus: true,
            onPressed: () {
              _add(context, MultiplayerJoinClosed(flow.id));
              _add(
                context,
                const LauncherSectionSelected(LauncherSection.setup),
              );
            },
            icon: const Icon(Icons.tune),
            label: const Text('Open Setup'),
          )
        else
          FilledButton.icon(
            key: const Key('join-try-again'),
            autofocus: true,
            onPressed: () => _add(context, MultiplayerJoinRetried(flow.id)),
            icon: const Icon(Icons.refresh),
            label: const Text('Try again'),
          ),
      ],
    );
  }
}
