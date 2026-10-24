part of '../screens.dart';

/// Why [text] is not a hosting port, in plain words, or null when it is one:
/// 0 to 65535, as the hosting defaults take it, but not the LAN discovery
/// port, which LAN discovery holds whenever it listens.
String? hostingPortProblem(String text) {
  final port = int.tryParse(text.trim());
  if (port == null || port < 0 || port > 65535 || text.trim() != text) {
    return 'Enter a number from 0 to 65535.';
  }
  if (port == LanBeacon.discoveryPort) {
    return 'Port ${LanBeacon.discoveryPort} is the LAN discovery port. Choose '
        'another.';
  }
  return null;
}

class _HostingPane extends StatelessWidget {
  const _HostingPane({required this.view, required this.preferences});

  final MultiplayerSettingsView view;
  final MultiplayerPreferences preferences;

  @override
  Widget build(BuildContext context) {
    final settings = preferences.settings;
    final editable = view.editable;
    void change(MultiplayerPreferencesChange change) =>
        _add(context, MultiplayerPreferencesEdited(change));
    return _SettingsGroup(
      title: 'Hosting defaults',
      icon: Icons.dns_outlined,
      children: [
        _SettingTextField(
          key: const Key('multiplayer-hosting-port'),
          label: 'Port',
          stored: '${preferences.hosting.port}',
          enabled: editable,
          problem: hostingPortProblem,
          numeric: true,
          helper:
              'The port a session you host uses. 0 picks any free port; the '
              'default is ${LanBeacon.defaultGamePort}.',
          onSave: (text) => change(
            MultiplayerPreferencesChange(hostingPort: int.parse(text)),
          ),
        ),
        SwitchListTile(
          key: const Key('multiplayer-upnp'),
          contentPadding: EdgeInsets.zero,
          value: settings.upnpEnabled,
          onChanged: editable
              ? (value) => change(MultiplayerPreferencesChange(upnp: value))
              : null,
          title: const Text('Open the port on my router (UPnP)'),
          subtitle: const Text(
            'Off by default. When you host, TopiaForge may ask your router to '
            'open the port.',
          ),
        ),
        SwitchListTile(
          key: const Key('multiplayer-lan-discovery'),
          contentPadding: EdgeInsets.zero,
          value: settings.lanDiscoveryEnabled,
          onChanged: editable
              ? (value) =>
                    change(MultiplayerPreferencesChange(lanDiscovery: value))
              : null,
          title: const Text('LAN discovery'),
          subtitle: Text(
            'On by default. Finds sessions on your local network by listening '
            'on UDP ${LanBeacon.discoveryPort}. Turning it off stops that at '
            'once.',
          ),
        ),
      ],
    );
  }
}

class _VoicePane extends StatelessWidget {
  const _VoicePane({required this.view, required this.preferences});

  final MultiplayerSettingsView view;
  final MultiplayerPreferences preferences;

  @override
  Widget build(BuildContext context) {
    final settings = preferences.settings;
    final editable = view.editable;
    void change(MultiplayerPreferencesChange change) =>
        _add(context, MultiplayerPreferencesEdited(change));
    if (!settings.hasVoiceConsent) {
      return _SettingsGroup(
        title: MultiplayerPrivacyText.voiceTitle,
        icon: Icons.mic_off_outlined,
        children: [
          const Text(
            'Voice chat is off. TopiaForge asks before it uses your '
            'microphone.',
            key: Key('voice-state'),
          ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            key: const Key('voice-consent'),
            onPressed: editable
                ? () async {
                    if (await showVoiceConsent(context) && context.mounted) {
                      change(
                        const MultiplayerPreferencesChange(voiceConsent: true),
                      );
                    }
                  }
                : null,
            icon: const Icon(Icons.mic_none),
            label: const Text('${MultiplayerPrivacyText.voiceOnButton}…'),
          ),
        ],
      );
    }
    return _SettingsGroup(
      title: MultiplayerPrivacyText.voiceTitle,
      icon: Icons.mic_none,
      children: [
        SwitchListTile(
          key: const Key('voice-switch'),
          contentPadding: EdgeInsets.zero,
          value: settings.voiceEnabled,
          onChanged: editable
              ? (value) => change(MultiplayerPreferencesChange(voice: value))
              : null,
          title: const Text(MultiplayerPrivacyText.voiceTitle),
          subtitle: const Text(
            'You agreed to voice chat. You can turn it off here at any time.',
          ),
        ),
        Text(
          'Withdrawing your consent turns voice chat off, and TopiaForge asks '
          'again before it uses your microphone.',
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const SizedBox(height: 8),
        OutlinedButton.icon(
          key: const Key('voice-withdraw'),
          onPressed: editable
              ? () => change(
                  const MultiplayerPreferencesChange(voiceConsent: false),
                )
              : null,
          icon: const Icon(Icons.mic_off_outlined),
          label: const Text('Withdraw consent'),
        ),
      ],
    );
  }
}

class _LinksPane extends StatelessWidget {
  const _LinksPane({required this.state, required this.preferences});

  final LauncherState state;
  final MultiplayerPreferences preferences;

  @override
  Widget build(BuildContext context) {
    final available = context.read<LauncherBloc>().hasLinkHandling;
    final status = state.multiplayer.linkHandling;
    final editable = available && state.multiplayer.settings.linksEditable;
    return _SettingsGroup(
      title: 'Links',
      icon: Icons.link,
      children: [
        SwitchListTile(
          key: const Key('multiplayer-links'),
          contentPadding: EdgeInsets.zero,
          value: preferences.settings.linkHandlingEnabled,
          onChanged: editable
              ? (value) => _add(context, SessionLinkHandlingChanged(value))
              : null,
          title: const Text('Open topiaforge:// links with TopiaForge'),
          subtitle: const Text(
            'On by default. A link opens TopiaForge, which asks you before '
            'anything is joined or installed.',
          ),
        ),
        Text(
          available
              ? _registrationLine(status)
              : 'Link handling is not available here.',
          key: const Key('links-registration'),
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ],
    );
  }
}

String _registrationLine(SessionLinkHandlingStatus? status) {
  if (status == null) return 'Checking the system registration…';
  return switch (status.registration) {
    SessionLinkRegistration.registered ||
    SessionLinkRegistration.alreadyRegistered =>
      'This computer opens topiaforge:// links with this launcher.',
    SessionLinkRegistration.removed || SessionLinkRegistration.notRegistered =>
      'This launcher does not open topiaforge:// links.',
    null =>
      status.enabled
          ? 'Checking the system registration…'
          : 'This launcher does not open topiaforge:// links.',
    SessionLinkRegistration.failed =>
      'The system registration did not change. ${status.detail}'.trim(),
    SessionLinkRegistration.managedBySystem ||
    SessionLinkRegistration.unsupported => status.detail,
  };
}

/// A setting typed as text, saved with its button or Enter. Saving is
/// offered only for a value that differs from [stored] and has no
/// [problem], which shows under the field as it is typed.
class _SettingTextField extends StatefulWidget {
  const _SettingTextField({
    super.key,
    required this.label,
    required this.stored,
    required this.enabled,
    required this.problem,
    required this.helper,
    required this.onSave,
    this.numeric = false,
  });

  final String label;
  final String stored;
  final bool enabled;
  final String? Function(String text) problem;
  final String helper;
  final ValueChanged<String> onSave;
  final bool numeric;

  @override
  State<_SettingTextField> createState() => _SettingTextFieldState();
}

class _SettingTextFieldState extends State<_SettingTextField> {
  late final _controller = TextEditingController(text: widget.stored);

  @override
  void didUpdateWidget(_SettingTextField old) {
    super.didUpdateWidget(old);
    // A new stored value, as one taken in from the game, replaces the text
    // unless the player has typed something else.
    if (old.stored != widget.stored && _controller.text == old.stored) {
      _controller.text = widget.stored;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  String get _text => _controller.text;

  bool get _savable =>
      widget.enabled && _text != widget.stored && widget.problem(_text) == null;

  void _save() {
    if (_savable) widget.onSave(_text);
  }

  @override
  Widget build(BuildContext context) {
    final problem = _text == widget.stored ? null : widget.problem(_text);
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Wrap(
        spacing: 10,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.start,
        children: [
          ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 320),
            child: TextField(
              controller: _controller,
              enabled: widget.enabled,
              keyboardType: widget.numeric ? TextInputType.number : null,
              inputFormatters: widget.numeric
                  ? [FilteringTextInputFormatter.digitsOnly]
                  : null,
              onChanged: (_) => setState(() {}),
              onSubmitted: (_) => _save(),
              decoration: InputDecoration(
                labelText: widget.label,
                helperText: problem == null ? widget.helper : null,
                helperMaxLines: 3,
                errorText: problem,
                errorMaxLines: 3,
              ),
            ),
          ),
          FilledButton(
            onPressed: _savable ? _save : null,
            child: const Text('Save'),
          ),
        ],
      ),
    );
  }
}
