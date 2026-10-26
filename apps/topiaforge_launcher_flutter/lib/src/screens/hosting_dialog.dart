part of '../screens.dart';

Future<void> _openHosting(BuildContext context) async {
  final bloc = context.read<LauncherBloc>();
  bloc.add(const HostingOpened());
  await showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (_) =>
        BlocProvider.value(value: bloc, child: const HostingDialog()),
  );
  if (!bloc.isClosed) bloc.add(const HostingClosed());
}

class HostingDialog extends StatelessWidget {
  const HostingDialog({super.key});

  @override
  Widget build(
    BuildContext context,
  ) => BlocConsumer<LauncherBloc, LauncherState>(
    listenWhen: (previous, current) =>
        previous.multiplayer.hosting.phase !=
            current.multiplayer.hosting.phase &&
        current.multiplayer.hosting.phase == HostingPhase.closed,
    listener: (context, _) => Navigator.of(context).pop(),
    builder: (context, state) {
      final view = state.multiplayer.hosting;
      void close() => _add(context, const HostingClosed());
      return PopScope(
        canPop: false,
        onPopInvokedWithResult: (popped, _) {
          if (!popped) close();
        },
        child: Actions(
          actions: {
            DismissIntent: CallbackAction<DismissIntent>(
              onInvoke: (_) {
                close();
                return null;
              },
            ),
          },
          child: AlertDialog(
            key: const Key('hosting-dialog'),
            title: Text(
              view.phase == HostingPhase.hosting ? 'Hosting' : 'Host a session',
            ),
            content: SizedBox(
              width: 620,
              child: SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    if (view.busy) ...[
                      const LinearProgressIndicator(),
                      const SizedBox(height: 12),
                      Text(switch (view.phase) {
                        HostingPhase.loading => 'Reading hosting preferences…',
                        HostingPhase.preparing =>
                          'Checking the profile and joinability…',
                        _ => 'Starting Robotopia…',
                      }),
                    ],
                    if (view.phase == HostingPhase.editing &&
                        view.preferences != null)
                      _HostingFields(
                        preferences: view.preferences!,
                        choices: view.choices,
                        developerMode: state.developerMode,
                      ),
                    if (view.phase == HostingPhase.review && view.plan != null)
                      _HostingReview(plan: view.plan!),
                    if (view.message.isNotEmpty)
                      SessionNotice(
                        message: view.message,
                        tone: view.phase == HostingPhase.failed
                            ? StatusTone.danger
                            : StatusTone.info,
                      ),
                    if (view.inviteCode case final code?) ...[
                      const SizedBox(height: 12),
                      SelectableText('Invite code: $code'),
                      SelectableText('topiaforge://join/$code'),
                      TextButton.icon(
                        onPressed: () => Clipboard.setData(
                          ClipboardData(text: 'topiaforge://join/$code'),
                        ),
                        icon: const Icon(Icons.copy),
                        label: const Text('Copy invite link'),
                      ),
                    ],
                    if (view.phase == HostingPhase.hosting &&
                        view.inviteCode == null)
                      const Text(
                        'This session is on the local network. It has no Internet invite.',
                      ),
                  ],
                ),
              ),
            ),
            actions: [
              TextButton(
                autofocus: true,
                onPressed: close,
                child: Text(
                  view.phase == HostingPhase.waiting ||
                          view.phase == HostingPhase.starting
                      ? 'Stop waiting'
                      : view.phase == HostingPhase.hosting
                      ? 'Close'
                      : 'Cancel',
                ),
              ),
              if (view.phase == HostingPhase.review ||
                  (view.phase == HostingPhase.failed &&
                      view.preferences != null))
                OutlinedButton(
                  onPressed: () => _add(context, const HostingEditRequested()),
                  child: const Text('Edit choices'),
                ),
              if (view.phase == HostingPhase.failed && view.preferences == null)
                OutlinedButton(
                  onPressed: () => _add(context, const HostingOpened()),
                  child: const Text('Try again'),
                ),
              if (view.canAllowDirect && view.choices != null)
                OutlinedButton(
                  onPressed: () => _allowDirect(context, view),
                  child: const Text('Allow a direct connection…'),
                ),
              if (view.phase == HostingPhase.review)
                FilledButton(
                  onPressed: () => _confirm(context, view),
                  child: const Text('Start hosting'),
                ),
            ],
          ),
        ),
      );
    },
  );

  Future<void> _confirm(BuildContext context, HostingView view) async {
    final plan = view.plan!;
    final acknowledged =
        !plan.preferences.settings.needsPrivacyDisclosure ||
        await showPrivacyDisclosure(context);
    if (!context.mounted ||
        !acknowledged ||
        !identical(
          context.read<LauncherBloc>().state.multiplayer.hosting.plan,
          plan,
        )) {
      return;
    }
    _add(context, const HostingConfirmed(acknowledgeDisclosure: true));
  }

  Future<void> _allowDirect(BuildContext context, HostingView view) async {
    final allowed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text(MultiplayerPrivacyText.directTitle),
        content: const SingleChildScrollView(
          child: Text(MultiplayerPrivacyText.directBody),
        ),
        actions: [
          TextButton(
            autofocus: true,
            onPressed: () => Navigator.pop(context, false),
            child: const Text(MultiplayerPrivacyText.directCancelButton),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text(MultiplayerPrivacyText.directAllowButton),
          ),
        ],
      ),
    );
    if (!context.mounted ||
        allowed != true ||
        context.read<LauncherBloc>().state.multiplayer.hosting.generation !=
            view.generation) {
      return;
    }
    final choices = view.choices!;
    _add(
      context,
      HostingReviewRequested(
        HostingChoices(
          defaults: choices.defaults,
          passwordRequired: choices.passwordRequired,
          servePackages: choices.servePackages,
          allowDirect: true,
        ),
      ),
    );
  }
}

class _HostingReview extends StatelessWidget {
  const _HostingReview({required this.plan});
  final HostingPlan plan;
  @override
  Widget build(BuildContext context) {
    final choices = plan.choices;
    final defaults = choices.defaults;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          '${withoutAddresses(defaults.name)} · ${defaults.maxPlayers} players',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        Text('Profile: ${withoutAddresses(plan.profile.name)}'),
        Text(plan.joinability),
        const SizedBox(height: 12),
        Text(
          choices.allowDirect
              ? 'Direct connection allowed for this session only. Other players can see your IP address.'
              : 'Hide my IP: on. This session uses a relay or stays on your local network.',
        ),
        Text(
          defaults.voice
              ? MultiplayerPrivacyText.sheetVoiceOn
              : MultiplayerPrivacyText.sheetVoiceOff,
        ),
        Text(
          choices.passwordRequired
              ? 'Robotopia will ask you to enter the password before hosting opens.'
              : 'No password required.',
        ),
        if (choices.servePackages)
          const Text(
            'Developer mode: the host may serve the exact mod archives to consenting players.',
          ),
        const SizedBox(height: 12),
        const Text(
          'The game starts with this profile. Hosting and any invite remain unconfirmed until the game reports them.',
        ),
      ],
    );
  }
}
