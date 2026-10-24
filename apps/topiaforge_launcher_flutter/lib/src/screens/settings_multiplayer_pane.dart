part of '../screens.dart';

/// Settings → Multiplayer: identity, privacy, hosting defaults, voice and
/// links, from the player's multiplayer preferences.
///
/// While a game that records in-game changes runs, the preferences it can
/// change are read-only, under one line that says so; links stay editable,
/// since the game never changes them. While the preferences cannot be read,
/// nothing of them shows: the pane says so and offers Try again.
class _MultiplayerSettingsPane extends StatefulWidget {
  const _MultiplayerSettingsPane({required this.state});

  final LauncherState state;

  @override
  State<_MultiplayerSettingsPane> createState() =>
      _MultiplayerSettingsPaneState();
}

class _MultiplayerSettingsPaneState extends State<_MultiplayerSettingsPane> {
  late final AppLifecycleListener _lifecycle;

  @override
  void initState() {
    super.initState();
    // Back at the launcher, the player may have closed the game, or started
    // it, so whether these can be changed is checked again.
    _lifecycle = AppLifecycleListener(
      onResume: () => _add(context, const MultiplayerGameCheckRequested()),
    );
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.state;
    final view = state.multiplayer.settings;
    final bloc = context.read<LauncherBloc>();
    final preferences = view.preferences;
    final List<Widget> children;
    if (!bloc.hasMultiplayer) {
      children = const [
        SessionNotice(
          message: 'Multiplayer settings are not available in this launcher.',
          tone: StatusTone.neutral,
        ),
      ];
    } else if (view.phase == MultiplayerPreferencesPhase.unreadable) {
      children = [
        SessionNotice(
          key: const Key('multiplayer-unreadable'),
          message:
              'Your multiplayer settings could not be read just now, so '
              'nothing was changed. Another program may be using the file.',
          tone: StatusTone.danger,
          action: OutlinedButton.icon(
            onPressed: () =>
                _add(context, const MultiplayerPreferencesRefreshRequested()),
            icon: const Icon(Icons.refresh),
            label: const Text('Try again'),
          ),
        ),
      ];
    } else if (preferences == null) {
      children = const [
        SessionNotice(
          key: Key('multiplayer-loading'),
          message: 'Loading your multiplayer settings…',
          icon: Icons.hourglass_empty,
        ),
        SizedBox(height: 8),
        LinearProgressIndicator(minHeight: 3),
      ];
    } else {
      children = [
        ..._multiplayerNotices(context, state, view, preferences),
        _IdentityPane(view: view, preferences: preferences),
        const SizedBox(height: 14),
        _PrivacyPane(view: view, preferences: preferences),
        const SizedBox(height: 14),
        _HostingPane(view: view, preferences: preferences),
        const SizedBox(height: 14),
        _VoicePane(view: view, preferences: preferences),
        const SizedBox(height: 14),
        _LinksPane(state: state, preferences: preferences),
      ];
    }
    return FocusTraversalGroup(
      child: SingleChildScrollView(
        key: const Key('multiplayer-settings-scroll'),
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: children,
        ),
      ),
    );
  }
}

/// The lines above the panes: why some are read-only, what recovery did,
/// and what last failed.
List<Widget> _multiplayerNotices(
  BuildContext context,
  LauncherState state,
  MultiplayerSettingsView view,
  MultiplayerPreferences preferences,
) {
  final checkAgain = OutlinedButton.icon(
    onPressed: () =>
        _add(context, const MultiplayerPreferencesRefreshRequested()),
    icon: const Icon(Icons.refresh),
    label: const Text('Check again'),
  );
  final notices = <Widget?>[
    switch (view.lock) {
      MultiplayerPreferencesLock.none => null,
      MultiplayerPreferencesLock.checking => const SessionNotice(
        key: Key('multiplayer-lock'),
        message: 'Checking whether Robotopia is running…',
        icon: Icons.hourglass_empty,
      ),
      MultiplayerPreferencesLock.gameRunning => SessionNotice(
        key: const Key('multiplayer-lock'),
        message:
            'Robotopia is running, so the settings it can change are '
            'read-only. You can change them once the game is closed.',
        icon: Icons.lock_outline,
        action: checkAgain,
      ),
      MultiplayerPreferencesLock.gameMaybeRunning => SessionNotice(
        key: const Key('multiplayer-lock'),
        message:
            'TopiaForge could not tell whether Robotopia is running, so the '
            'settings it can change are read-only. You can change them once '
            'the game is closed.',
        tone: StatusTone.warning,
        icon: Icons.lock_outline,
        action: checkAgain,
      ),
    },
    switch (preferences.health) {
      MultiplayerPreferencesHealth.salvaged => const SessionNotice(
        key: Key('multiplayer-recovered'),
        message:
            'Part of your multiplayer settings was damaged. TopiaForge kept a '
            'copy of the file and uses what it could read.',
        tone: StatusTone.warning,
      ),
      MultiplayerPreferencesHealth.reset => const SessionNotice(
        key: Key('multiplayer-recovered'),
        message:
            'Your multiplayer settings were damaged, so TopiaForge kept a copy '
            'of the file and uses the defaults.',
        tone: StatusTone.warning,
      ),
      _ => null,
    },
    if (view.problem case final problem?)
      SessionNotice(
        key: const Key('multiplayer-problem'),
        message: problem,
        tone: StatusTone.danger,
      ),
    if (state.gameInstall == null)
      const SessionNotice(
        key: Key('multiplayer-no-install'),
        message:
            'These settings reach Robotopia once you select its install in '
            'Setup.',
        tone: StatusTone.neutral,
      ),
  ].nonNulls;
  return [
    for (final notice in notices) ...[notice, const SizedBox(height: 12)],
  ];
}

/// A pane of Settings → Multiplayer: a title, and its rows.
class _SettingsGroup extends StatelessWidget {
  const _SettingsGroup({
    required this.title,
    required this.icon,
    required this.children,
  });

  final String title;
  final IconData icon;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(icon, size: 18, color: TopiaForgePalette.launchDark),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  title,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          ...children,
        ],
      ),
    );
  }
}

/// Why [name] is not a display name the domain allows, in plain words, or
/// null when it is one. [MultiplayerSettings.isValidDisplayName] decides;
/// this only says why.
String? displayNameProblem(String name) {
  if (MultiplayerSettings.isValidDisplayName(name)) return null;
  const minimum = MultiplayerSettings.minimumDisplayNameLength;
  const maximum = MultiplayerSettings.maximumDisplayNameLength;
  if (name.length < minimum) {
    return 'Use at least $minimum characters.';
  }
  if (name.length > maximum) return 'Use at most $maximum characters.';
  if (name.trim() != name) {
    return 'Take out the space at the start or the end.';
  }
  return 'Use letters, numbers, spaces and symbols only.';
}

class _IdentityPane extends StatelessWidget {
  const _IdentityPane({required this.view, required this.preferences});

  final MultiplayerSettingsView view;
  final MultiplayerPreferences preferences;

  @override
  Widget build(BuildContext context) {
    return _SettingsGroup(
      title: 'Identity',
      icon: Icons.badge_outlined,
      children: [
        _SettingTextField(
          key: const Key('multiplayer-display-name'),
          label: 'Display name',
          stored: preferences.settings.displayName,
          enabled: view.editable,
          problem: displayNameProblem,
          helper:
              '${MultiplayerSettings.minimumDisplayNameLength} to '
              '${MultiplayerSettings.maximumDisplayNameLength} characters. '
              'Other players see this name.',
          onSave: (name) => _add(
            context,
            MultiplayerPreferencesEdited(
              MultiplayerPreferencesChange(displayName: name),
            ),
          ),
        ),
      ],
    );
  }
}

class _PrivacyPane extends StatelessWidget {
  const _PrivacyPane({required this.view, required this.preferences});

  final MultiplayerSettingsView view;
  final MultiplayerPreferences preferences;

  @override
  Widget build(BuildContext context) {
    final settings = preferences.settings;
    final read = !settings.needsPrivacyDisclosure;
    return _SettingsGroup(
      title: 'Privacy',
      icon: Icons.shield_outlined,
      children: [
        const Wrap(
          spacing: 10,
          runSpacing: 6,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(MultiplayerPrivacyText.hideMyIp),
            StatusPill(
              key: Key('hide-my-ip'),
              label: 'On',
              tone: StatusTone.good,
              icon: Icons.lock,
            ),
          ],
        ),
        const SizedBox(height: 8),
        const Text(MultiplayerPrivacyText.disclosureHidden),
        const SizedBox(height: 6),
        const Text(MultiplayerPrivacyText.disclosureChoice),
        const SizedBox(height: 12),
        Text(
          read
              ? 'You read the address disclosure (version '
                    '${settings.acknowledgedPrivacyDisclosure}).'
              : 'You have not read the address disclosure yet. TopiaForge '
                    'shows it before you first host or join.',
          key: const Key('disclosure-record'),
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const SizedBox(height: 4),
        Text(
          'Robotopia keeps its own record of this disclosure, so the game '
          'may show it to you once as well.',
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const SizedBox(height: 8),
        OutlinedButton.icon(
          key: const Key('read-disclosure'),
          onPressed: () async {
            final understood = await showPrivacyDisclosure(context);
            // Reading it again changes nothing; reading it the first time
            // records it, as before a first host or join.
            if (understood && !read && view.editable && context.mounted) {
              _add(
                context,
                const MultiplayerPreferencesEdited(
                  MultiplayerPreferencesChange(acknowledgeDisclosure: true),
                ),
              );
            }
          },
          icon: const Icon(Icons.menu_book_outlined),
          label: Text(read ? 'Read it again' : 'Read it'),
        ),
      ],
    );
  }
}
