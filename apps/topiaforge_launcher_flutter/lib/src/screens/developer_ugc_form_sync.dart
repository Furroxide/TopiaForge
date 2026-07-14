part of '../screens.dart';

extension _UgcLiveSyncFormSync on _UgcLiveSyncPaneState {
  void _syncFormFromState(LauncherState oldState) {
    final root = widget.state.developerWorkspace?.projectRoot ?? '';
    final previous = oldState.ugcLiveSync;
    final current = widget.state.ugcLiveSync;
    if (root != _projectRoot) {
      _projectRoot = root;
      _replaceFormValues(current);
      return;
    }

    _replaceTextIfUnedited(
      _watchFolder,
      previous.watchFolder,
      current.watchFolder,
    );
    _replaceTextIfUnedited(
      _editorUrl,
      _editorValue(previous),
      _editorValue(current),
    );
    _replaceTextIfUnedited(_sceneId, previous.sceneId, current.sceneId);
    if (_transport == previous.transport) {
      _transport = current.transport;
    }
    if (_autoConnect == previous.autoConnectOnStart) {
      _autoConnect = current.autoConnectOnStart;
    }
  }

  void _replaceFormValues(UgcLiveSyncSettings settings) {
    _watchFolder.text = settings.watchFolder;
    _editorUrl.text = _editorValue(settings);
    _sceneId.text = settings.sceneId;
    _transport = settings.transport;
    _autoConnect = settings.autoConnectOnStart;
  }

  void _replaceTextIfUnedited(
    TextEditingController controller,
    String previous,
    String current,
  ) {
    if (controller.text == current) {
      return;
    }
    if (controller.text == previous || controller.text.trim() == current) {
      controller.text = current;
    }
  }

  String _editorValue(UgcLiveSyncSettings settings) =>
      settings.editorUrl.isNotEmpty ? settings.editorUrl : settings.documentUrl;
}
