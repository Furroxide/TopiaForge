part of '../local_launcher_repository.dart';

GameInstallDiscoveryService _defaultGameInstallDiscovery(String? knownPath) {
  final path = knownPath?.trim() ?? '';
  return path.isEmpty
      ? GameInstallDiscoveryService.standard()
      : GameInstallDiscoveryService(adapters: const []);
}

extension _GameInstallDiscoveryHelpers on LocalLauncherRepository {
  Future<List<GameInstallCandidate>> _discoverGameInstalls() async {
    _ensureDataRoot();
    return _discoverGameInstallsFromSettings(await _loadSettings());
  }

  Future<GameInstall?> _detectKnownInstall() async =>
      (await _discoverGameInstalls()).firstOrNull?.install;

  Future<({GameInstall? install, List<GameInstallCandidate> candidates})>
  _resolveGameInstallDiscovery(Map<String, Object?> settings) async {
    final candidates = await _discoverGameInstallsFromSettings(settings);
    final configured = settings['gamePath'];
    final configuredPath = configured is String ? configured.trim() : '';
    final explicitPath = _knownGamePath?.trim() ?? '';
    final savedCandidate = candidates
        .where(
          (candidate) => candidate.sources.any(
            (source) =>
                source.id == GameInstallDiscoverySources.savedSelection.id,
          ),
        )
        .firstOrNull;
    final install = explicitPath.isNotEmpty
        ? candidates.firstOrNull?.install ??
              await _validateGameDirectory(explicitPath)
        : configuredPath.isNotEmpty
        ? savedCandidate?.install ??
              await _validateGameDirectory(configuredPath)
        : candidates.firstOrNull?.install;
    return (install: install, candidates: candidates);
  }

  Future<GameInstall> _selectGameDirectory(String path) async {
    final selectedPath = Directory(path).absolute.path;
    var install = await _validateGameDirectory(path);
    _requireSelectableInstall(install);
    final canonicalPath = await Directory(install.path).resolveSymbolicLinks();
    install = await _validateGameDirectory(canonicalPath);
    _requireSelectableInstall(install);
    await _updateSettings((settings) => settings['gamePath'] = selectedPath);
    await _appendLauncherLogBestEffort(
      'Selected game directory $selectedPath.',
    );
    return install;
  }

  Future<List<GameInstallCandidate>> _discoverGameInstallsFromSettings(
    Map<String, Object?> settings,
  ) {
    final configured = settings['gamePath'];
    final selectedPath = configured is String ? configured.trim() : '';
    final explicitPath = _knownGamePath?.trim() ?? '';
    return _gameInstallDiscovery.discover(
      validate: _validateGameDirectory,
      additionalAdapters: [
        if (explicitPath.isNotEmpty)
          FixedGameInstallDiscoveryAdapter(
            path: explicitPath,
            source: GameInstallDiscoverySources.injectedPath,
          )
        else if (selectedPath.isNotEmpty)
          FixedGameInstallDiscoveryAdapter(
            path: selectedPath,
            source: GameInstallDiscoverySources.savedSelection,
          ),
      ],
    );
  }
}

void _requireSelectableInstall(GameInstall install) {
  if (install.issues.any((issue) => issue.isBlocking)) {
    throw StateError(install.issues.map((issue) => issue.message).join(' '));
  }
}
