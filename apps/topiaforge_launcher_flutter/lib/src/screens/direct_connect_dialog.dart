part of '../screens.dart';

/// Asks for a server's host and port, and joins it through the join flow,
/// which asks before it contacts anything. The address stays in this dialog
/// and its history rows: the flow and the status bar never show it.
Future<void> showDirectConnectDialog(BuildContext context) async {
  final bloc = context.read<LauncherBloc>();
  final target = await showDialog<JoinAddressTarget>(
    context: context,
    builder: (_) => _DirectConnectDialog(
      history: bloc.state.multiplayer.join.typedAddresses,
    ),
  );
  if (target != null && !bloc.isClosed) {
    bloc.add(MultiplayerJoinRequested(target, origin: JoinFlowOrigin.address));
  }
}

class _DirectConnectDialog extends StatefulWidget {
  const _DirectConnectDialog({required this.history});

  final List<String> history;

  @override
  State<_DirectConnectDialog> createState() => _DirectConnectDialogState();
}

class _DirectConnectDialogState extends State<_DirectConnectDialog> {
  final _host = TextEditingController();
  final _port = TextEditingController(text: '${LanBeacon.defaultGamePort}');
  var _tried = false;

  @override
  void dispose() {
    _host.dispose();
    _port.dispose();
    super.dispose();
  }

  /// The target the fields name, or null while they name none.
  JoinAddressTarget? get _target {
    final host = _host.text.trim();
    final port = _port.text.trim();
    if (host.isEmpty) return null;
    if (port.isEmpty) return JoinAddressTarget.tryParse(host);
    if (!RegExp(r'^[0-9]{1,5}$').hasMatch(port)) return null;
    final bare = host.startsWith('[') && host.endsWith(']')
        ? host.substring(1, host.length - 1)
        : host;
    return JoinAddressTarget.tryParse(
      bare.contains(':') ? '[$bare]:$port' : '$bare:$port',
    );
  }

  String? get _problem {
    if (!_tried) return null;
    if (_host.text.trim().isEmpty) return 'Type the server\'s address.';
    final port = int.tryParse(_port.text.trim());
    if (_port.text.trim().isNotEmpty &&
        (port == null || port < 1 || port > 65535)) {
      return 'A port is a number from 1 to 65535.';
    }
    return _target == null
        ? 'That is not an address. Type an IP address or a host name.'
        : null;
  }

  void _join() {
    setState(() => _tried = true);
    final target = _target;
    if (target != null) Navigator.of(context).pop(target);
  }

  void _use(String entry) {
    final parsed = JoinAddressTarget.tryParse(entry);
    if (parsed == null) return;
    setState(() {
      _host.text = parsed.host;
      _port.text = '${parsed.port}';
      _tried = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final problem = _problem;
    return AlertDialog(
      key: const Key('direct-connect-dialog'),
      title: const Text('Join by address'),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text(
                'Type the address the host gave you. Nothing is contacted '
                'until you choose Join, and the launcher asks before a '
                'connection would show your IP address.',
              ),
              const SizedBox(height: 14),
              LayoutBuilder(
                builder: (context, constraints) {
                  final host = TextField(
                    key: const Key('direct-connect-host'),
                    controller: _host,
                    autofocus: true,
                    autocorrect: false,
                    enableSuggestions: false,
                    textInputAction: TextInputAction.next,
                    decoration: const InputDecoration(
                      labelText: 'Host',
                      hintText: 'IP address or host name',
                    ),
                    onChanged: (_) => setState(() {}),
                    onSubmitted: (_) => _join(),
                  );
                  final port = TextField(
                    key: const Key('direct-connect-port'),
                    controller: _port,
                    keyboardType: TextInputType.number,
                    inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                    decoration: const InputDecoration(labelText: 'Port'),
                    onChanged: (_) => setState(() {}),
                    onSubmitted: (_) => _join(),
                  );
                  if (constraints.maxWidth < 340 ||
                      MediaQuery.textScalerOf(context).scale(1) > 1.3) {
                    return Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [host, const SizedBox(height: 10), port],
                    );
                  }
                  return Row(
                    children: [
                      Expanded(flex: 3, child: host),
                      const SizedBox(width: 10),
                      Expanded(child: port),
                    ],
                  );
                },
              ),
              if (problem != null) ...[
                const SizedBox(height: 6),
                Text(
                  problem,
                  key: const Key('direct-connect-problem'),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: TopiaForgePalette.danger,
                  ),
                ),
              ],
              const SizedBox(height: 12),
              const _SheetLine(
                icon: Icons.password,
                text:
                    'If the server has a password, Robotopia asks for it when '
                    'you join. The launcher never asks for it or stores it.',
              ),
              if (widget.history.isNotEmpty) ...[
                const SizedBox(height: 14),
                Text('Recent addresses', style: theme.textTheme.titleSmall),
                const SizedBox(height: 4),
                for (var index = 0; index < widget.history.length; index++)
                  ListTile(
                    key: Key('direct-connect-history-$index'),
                    leading: const Icon(Icons.history),
                    title: Text(widget.history[index]),
                    onTap: () => _use(widget.history[index]),
                  ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          key: const Key('direct-connect-cancel'),
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton.icon(
          key: const Key('direct-connect-join'),
          onPressed: _join,
          icon: const Icon(Icons.login),
          label: const Text('Join'),
        ),
      ],
    );
  }
}
