part of '../screens.dart';

class _UgcLiveSyncPane extends StatefulWidget {
  const _UgcLiveSyncPane({required this.state});

  final LauncherState state;

  @override
  State<_UgcLiveSyncPane> createState() => _UgcLiveSyncPaneState();
}

class _UgcLiveSyncPaneState extends State<_UgcLiveSyncPane> {
  late final TextEditingController _watchFolder;
  late final TextEditingController _editorUrl;
  late final TextEditingController _sceneId;
  late String _transport;
  late bool _autoConnect;
  String _projectRoot = '';

  @override
  void initState() {
    super.initState();
    final settings = widget.state.ugcLiveSync;
    _watchFolder = TextEditingController(text: settings.watchFolder);
    _editorUrl = TextEditingController(
      text: settings.editorUrl.isNotEmpty
          ? settings.editorUrl
          : settings.documentUrl,
    );
    _sceneId = TextEditingController(text: settings.sceneId);
    _transport = settings.transport;
    _autoConnect = settings.autoConnectOnStart;
    _projectRoot = widget.state.developerWorkspace?.projectRoot ?? '';
    // Pull the live diagnostics (game status + watch-folder scenes) as soon as the cockpit appears.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        _add(context, const DeveloperUgcStatusRefreshed());
      }
    });
  }

  @override
  void didUpdateWidget(covariant _UgcLiveSyncPane oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Reload the fields when the active project changes (e.g. after scan/create), but leave in-progress edits
    // untouched within the same project.
    final root = widget.state.developerWorkspace?.projectRoot ?? '';
    if (root != _projectRoot) {
      _projectRoot = root;
      final settings = widget.state.ugcLiveSync;
      _watchFolder.text = settings.watchFolder;
      _editorUrl.text = settings.editorUrl.isNotEmpty
          ? settings.editorUrl
          : settings.documentUrl;
      _sceneId.text = settings.sceneId;
      setState(() {
        _transport = settings.transport;
        _autoConnect = settings.autoConnectOnStart;
      });
    }
  }

  @override
  void dispose() {
    _watchFolder.dispose();
    _editorUrl.dispose();
    _sceneId.dispose();
    super.dispose();
  }

  void _save() {
    final automerge = _transport == 'automerge';
    final url = _editorUrl.text.trim();
    _add(
      context,
      DeveloperUgcSettingsSaved(
        transport: _transport,
        watchFolder: _watchFolder.text.trim(),
        editorUrl: automerge ? url : '',
        documentUrl: automerge ? url : '',
        sceneId: _sceneId.text.trim(),
        autoConnectOnStart: _autoConnect,
      ),
    );
  }

  Future<void> _browse() async {
    final path = await getDirectoryPath(
      confirmButtonText: 'Select UGC watch folder',
    );
    if (path != null && mounted) {
      setState(() => _watchFolder.text = path);
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.state;
    final hasProject = state.developerWorkspace?.hasProject == true;
    final hasInstall = state.gameInstall != null;
    final busy = state.isBusy;

    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'UGC Live Sync',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              IconButton(
                tooltip: 'Refresh diagnostics',
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperUgcStatusRefreshed()),
                icon: const Icon(Icons.refresh, size: 18),
              ),
              StatusPill(
                label: state.ugcPublisherRunning
                    ? 'Publisher running'
                    : 'Publisher stopped',
                tone: state.ugcPublisherRunning
                    ? StatusTone.good
                    : StatusTone.neutral,
                icon: state.ugcPublisherRunning
                    ? Icons.sync
                    : Icons.sync_disabled,
              ),
            ],
          ),
          const SizedBox(height: 10),
          _diagnostics(state),
          const SizedBox(height: 12),
          if (!hasProject)
            const Text(
              'Create or scan a developer project to configure UGC live sync.',
            )
          else ...[
            const Text(
              'Author UGC content in the Unity companion and hot-reload it into the running game. Connection '
              'values are auto-detected: starting the publisher captures the live document URL and deploys it for you.',
              style: TextStyle(
                color: QuantumWorksPalette.mutedText,
                fontSize: 12,
              ),
            ),
            const SizedBox(height: 12),
            _watchFolderRow(state, busy),
            const SizedBox(height: 10),
            _transportAndScene(state, busy),
            const SizedBox(height: 10),
            if (state.ugcCapturedDocumentUrl.isNotEmpty) ...[
              _capturedDocument(state),
              const SizedBox(height: 10),
            ],
            Row(
              children: [
                Checkbox(
                  value: _autoConnect,
                  onChanged: busy
                      ? null
                      : (value) =>
                            setState(() => _autoConnect = value ?? false),
                ),
                const Text('Auto-connect on launch'),
              ],
            ),
            _advancedEditorUrl(busy),
            const SizedBox(height: 12),
            _actions(state, busy, hasInstall),
            if (!hasInstall) ...[
              const SizedBox(height: 8),
              const Text(
                'Detect a Robotopia install (Setup tab) to deploy the runtime config and Go Live.',
                style: TextStyle(
                  color: QuantumWorksPalette.mutedText,
                  fontSize: 12,
                ),
              ),
            ],
            if (state.ugcSidecarLog.isNotEmpty) ...[
              const SizedBox(height: 12),
              _sidecarLog(state),
            ],
          ],
        ],
      ),
    );
  }

  // A row of live status chips fed by the game handshake, the publisher, and the watch-folder scene scan.
  Widget _diagnostics(LauncherState state) {
    final status = state.ugcStatus;
    final gameLive = status?.isLive == true;
    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        StatusPill(
          label: status == null
              ? 'Game: unknown'
              : (gameLive ? 'Game: ${status.status}' : 'Game idle'),
          tone: gameLive ? StatusTone.good : StatusTone.neutral,
          icon: gameLive ? Icons.videogame_asset : Icons.videogame_asset_off,
        ),
        StatusPill(
          label: '${state.ugcScenes.length} scene(s)',
          tone: state.ugcScenes.isEmpty ? StatusTone.neutral : StatusTone.good,
          icon: Icons.layers_outlined,
        ),
        if (state.ugcCapturedDocumentUrl.isNotEmpty)
          const StatusPill(
            label: 'Document captured',
            tone: StatusTone.good,
            icon: Icons.link,
          ),
        if (status != null && status.lastAppliedUtc.isNotEmpty)
          StatusPill(
            label: 'Last applied ${_shortTime(status.lastAppliedUtc)}',
            tone: StatusTone.good,
            icon: Icons.history,
          ),
      ],
    );
  }

  Widget _watchFolderRow(LauncherState state, bool busy) {
    final hasDefault = (state.ugcStatus?.defaultWatchFolder ?? '').isNotEmpty;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Expanded(
          child: TextField(
            controller: _watchFolder,
            decoration: const InputDecoration(
              labelText: 'Watch folder',
              isDense: true,
            ),
          ),
        ),
        const SizedBox(width: 8),
        if (hasDefault)
          TextButton(
            onPressed: busy
                ? null
                : () => setState(
                    () =>
                        _watchFolder.text = state.ugcStatus!.defaultWatchFolder,
                  ),
            child: const Text('Use game default'),
          ),
        const SizedBox(width: 4),
        OutlinedButton(
          onPressed: busy ? null : _browse,
          child: const Text('Browse'),
        ),
      ],
    );
  }

  Widget _transportAndScene(LauncherState state, bool busy) {
    return Wrap(
      spacing: 16,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Transport'),
            const SizedBox(width: 10),
            DropdownButton<String>(
              value: _transport == 'automerge' ? 'automerge' : 'localFolder',
              items: const [
                DropdownMenuItem(
                  value: 'localFolder',
                  child: Text('Local folder'),
                ),
                DropdownMenuItem(value: 'automerge', child: Text('Automerge')),
              ],
              onChanged: busy
                  ? null
                  : (value) =>
                        setState(() => _transport = value ?? 'localFolder'),
            ),
          ],
        ),
        _sceneSelector(state, busy),
      ],
    );
  }

  // A dropdown when scenes were auto-detected from the watch folder; a free-text field otherwise.
  Widget _sceneSelector(LauncherState state, bool busy) {
    if (state.ugcScenes.isEmpty) {
      return SizedBox(
        width: 220,
        child: TextField(
          controller: _sceneId,
          decoration: const InputDecoration(
            labelText: 'Scene id (optional)',
            isDense: true,
          ),
        ),
      );
    }
    final ids = state.ugcScenes.map((scene) => scene.id).toList();
    final current = _sceneId.text.trim();
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        const Text('Scene'),
        const SizedBox(width: 10),
        DropdownButton<String>(
          value: ids.contains(current) ? current : null,
          hint: const Text('First scene'),
          items: [
            for (final scene in state.ugcScenes)
              DropdownMenuItem(value: scene.id, child: Text(scene.label)),
          ],
          onChanged: busy
              ? null
              : (value) => setState(() => _sceneId.text = value ?? ''),
        ),
      ],
    );
  }

  Widget _capturedDocument(LauncherState state) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: const Color(0x14888888),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            'Captured live document (auto-deployed to the game)',
            style: TextStyle(
              color: QuantumWorksPalette.mutedText,
              fontSize: 11,
            ),
          ),
          const SizedBox(height: 4),
          SelectableText(
            state.ugcCapturedDocumentUrl,
            style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
          ),
        ],
      ),
    );
  }

  Widget _advancedEditorUrl(bool busy) {
    return Theme(
      data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
      child: ExpansionTile(
        tilePadding: EdgeInsets.zero,
        childrenPadding: const EdgeInsets.only(bottom: 8),
        title: const Text(
          'Advanced — manual editor / document URL',
          style: TextStyle(fontSize: 12, color: QuantumWorksPalette.mutedText),
        ),
        children: [
          TextField(
            controller: _editorUrl,
            enabled: !busy,
            decoration: const InputDecoration(
              labelText: 'Editor / document URL (Automerge)',
              isDense: true,
              hintText: 'https://host/?project=automerge:...&scene=main',
              helperText:
                  'Usually auto-captured from the publisher — only set this to connect to an existing document.',
            ),
          ),
        ],
      ),
    );
  }

  Widget _actions(LauncherState state, bool busy, bool hasInstall) {
    return Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        FilledButton.icon(
          onPressed: busy || !hasInstall
              ? null
              : () => _add(context, const DeveloperUgcGoLive()),
          icon: const Icon(Icons.rocket_launch_outlined),
          label: const Text('Go Live'),
        ),
        OutlinedButton.icon(
          onPressed: busy ? null : _save,
          icon: const Icon(Icons.save_outlined),
          label: const Text('Save'),
        ),
        OutlinedButton.icon(
          onPressed: busy || !hasInstall
              ? null
              : () => _add(context, const DeveloperUgcConfigDeployed()),
          icon: const Icon(Icons.cloud_upload_outlined),
          label: const Text('Deploy to game'),
        ),
        OutlinedButton.icon(
          onPressed: busy
              ? null
              : () => _add(context, const DeveloperWatchFolderOpened()),
          icon: const Icon(Icons.folder_open),
          label: const Text('Open watch folder'),
        ),
        OutlinedButton.icon(
          onPressed: busy
              ? null
              : () => _add(context, const DeveloperUgcPublishToggled()),
          icon: Icon(
            state.ugcPublisherRunning
                ? Icons.stop_circle_outlined
                : Icons.podcasts,
          ),
          label: Text(
            state.ugcPublisherRunning
                ? 'Stop Automerge publisher'
                : 'Publish via Automerge',
          ),
        ),
      ],
    );
  }

  Widget _sidecarLog(LauncherState state) {
    return Theme(
      data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
      child: ExpansionTile(
        tilePadding: EdgeInsets.zero,
        title: Text(
          'Publisher log (${state.ugcSidecarLog.length})',
          style: const TextStyle(fontSize: 12),
        ),
        children: [
          Container(
            width: double.infinity,
            height: 120,
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              color: const Color(0x59000000),
              borderRadius: BorderRadius.circular(6),
            ),
            child: SingleChildScrollView(
              reverse: true,
              child: SelectableText(
                state.ugcSidecarLog.join('\n'),
                style: const TextStyle(
                  fontFamily: 'monospace',
                  fontSize: 11,
                  color: Colors.white70,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  String _shortTime(String iso) {
    final parsed = DateTime.tryParse(iso);
    if (parsed == null) {
      return iso;
    }
    final local = parsed.toLocal();
    String two(int value) => value.toString().padLeft(2, '0');
    return '${two(local.hour)}:${two(local.minute)}:${two(local.second)}';
  }
}
