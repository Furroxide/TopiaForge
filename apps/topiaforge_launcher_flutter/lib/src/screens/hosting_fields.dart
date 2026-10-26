part of '../screens.dart';

class _HostingFields extends StatefulWidget {
  const _HostingFields({
    required this.preferences,
    this.choices,
    required this.developerMode,
  });
  final MultiplayerPreferences preferences;
  final HostingChoices? choices;
  final bool developerMode;
  @override
  State<_HostingFields> createState() => _HostingFieldsState();
}

class _HostingFieldsState extends State<_HostingFields> {
  final _form = GlobalKey<FormState>();
  late final TextEditingController _name, _port;
  late int _players;
  late ServerVisibility _visibility;
  late bool _voice, _password, _serve;
  String? _problem;
  @override
  void initState() {
    super.initState();
    final defaults = widget.choices?.defaults ?? widget.preferences.hosting;
    _name = TextEditingController(text: defaults.name);
    _port = TextEditingController(text: '${defaults.port}');
    _players = defaults.maxPlayers;
    _visibility = defaults.visibility;
    _voice = defaults.voice && widget.preferences.settings.voiceActive;
    _password = widget.choices?.passwordRequired ?? false;
    _serve = widget.developerMode && (widget.choices?.servePackages ?? false);
  }

  @override
  void dispose() {
    _name.dispose();
    _port.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Form(
    key: _form,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TextFormField(
          key: const Key('hosting-name'),
          controller: _name,
          decoration: const InputDecoration(labelText: 'Server name'),
          maxLength: ServerDescriptorV1.maximumNameLength,
          validator: (value) => value == null || value.trim().isEmpty
              ? 'Enter a server name.'
              : null,
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<int>(
          initialValue: _players,
          decoration: const InputDecoration(labelText: 'Maximum players'),
          items: [
            for (var count = 2; count <= 16; count++)
              DropdownMenuItem(value: count, child: Text('$count')),
          ],
          onChanged: (value) => setState(() => _players = value!),
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<ServerVisibility>(
          initialValue: _visibility,
          decoration: const InputDecoration(labelText: 'Visibility'),
          items: [
            for (final value in ServerVisibility.values)
              DropdownMenuItem(
                value: value,
                child: Text(switch (value) {
                  ServerVisibility.public => 'Public',
                  ServerVisibility.invite => 'Invite only',
                  ServerVisibility.lan => 'Local network (LAN)',
                }),
              ),
          ],
          onChanged: (value) => setState(() => _visibility = value!),
        ),
        const SizedBox(height: 12),
        TextFormField(
          key: const Key('hosting-port'),
          controller: _port,
          keyboardType: TextInputType.number,
          decoration: const InputDecoration(
            labelText: 'Hosting port',
            helperText: '0 chooses a free port. LAN discovery uses 27251.',
          ),
          validator: (value) {
            final port = int.tryParse(value ?? '');
            return port == null ||
                    port < 0 ||
                    port > 65535 ||
                    port == LanBeacon.discoveryPort
                ? 'Choose 0–65535, excluding 27251.'
                : null;
          },
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Require a password'),
          subtitle: const Text(
            'Enter it in Robotopia; the launcher never stores it.',
          ),
          value: _password,
          onChanged: (value) => setState(() => _password = value),
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Voice chat for this session'),
          subtitle: Text(
            widget.preferences.settings.voiceActive
                ? MultiplayerPrivacyText.sheetVoiceOn
                : 'Voice is off. Microphone consent and voice settings are in Settings → Multiplayer.',
          ),
          value: _voice,
          onChanged: widget.preferences.settings.voiceActive
              ? (value) => setState(() => _voice = value)
              : null,
        ),
        if (widget.developerMode)
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('Serve mod packages (developer mode)'),
            value: _serve,
            onChanged: (value) => setState(() => _serve = value),
          ),
        const Text(
          'Hide my IP is on. If Internet play needs a direct connection, TopiaForge asks separately for this session.',
        ),
        if (_problem != null) Text(_problem!),
        const SizedBox(height: 16),
        FilledButton.icon(
          onPressed: _review,
          icon: const Icon(Icons.fact_check),
          label: const Text('Check joinability'),
        ),
      ],
    ),
  );

  void _review() {
    if (!_form.currentState!.validate()) return;
    try {
      final defaults = MultiplayerHostingDefaults(
        name: _name.text.trim(),
        maxPlayers: _players,
        visibility: _visibility,
        port: int.parse(_port.text),
        voice: _voice,
      );
      _add(
        context,
        HostingReviewRequested(
          HostingChoices(
            defaults: defaults,
            passwordRequired: _password,
            servePackages: _serve,
          ),
        ),
      );
    } on Object {
      setState(() => _problem = 'Check the server name and hosting choices.');
    }
  }
}
