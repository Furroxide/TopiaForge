import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

part 'steam_game_install_discovery.dart';

typedef GameInstallValidator = Future<GameInstall> Function(String path);

/// Produces possible Robotopia install roots without validating game files.
///
/// Adapters are intentionally small and injectable. Validation, canonical
/// layout resolution, and de-duplication belong to
/// [GameInstallDiscoveryService].
abstract interface class GameInstallDiscoveryAdapter {
  GameInstallDiscoverySource get source;

  Future<List<String>> discoverPaths();
}

/// Fixed path adapter used for a saved selection or an explicitly injected
/// path. It never searches the filesystem.
class FixedGameInstallDiscoveryAdapter implements GameInstallDiscoveryAdapter {
  const FixedGameInstallDiscoveryAdapter({
    required this.path,
    required this.source,
  });

  final String path;

  @override
  final GameInstallDiscoverySource source;

  @override
  Future<List<String>> discoverPaths() async => [path];
}

/// Reads the documented Robotopia path override and nothing else.
class EnvironmentGameInstallDiscoveryAdapter
    implements GameInstallDiscoveryAdapter {
  EnvironmentGameInstallDiscoveryAdapter({
    Map<String, String>? environment,
    this.variableName = 'ROBOTOPIA_GAME_DIR',
  }) : _environment = environment ?? Platform.environment;

  final Map<String, String> _environment;
  final String variableName;

  @override
  GameInstallDiscoverySource get source =>
      GameInstallDiscoverySources.environment;

  @override
  Future<List<String>> discoverPaths() async {
    final path = _environment[variableName]?.trim() ?? '';
    return path.isEmpty ? const [] : [path];
  }
}

/// Returns only Tomato Cake's documented per-platform install location.
class TomatoCakeGameInstallDiscoveryAdapter
    implements GameInstallDiscoveryAdapter {
  TomatoCakeGameInstallDiscoveryAdapter({
    Map<String, String>? environment,
    String? hostPlatform,
  }) : _environment = environment ?? Platform.environment,
       _hostPlatform = hostPlatform ?? Platform.operatingSystem;

  final Map<String, String> _environment;
  final String _hostPlatform;

  @override
  GameInstallDiscoverySource get source =>
      GameInstallDiscoverySources.tomatoCake;

  @override
  Future<List<String>> discoverPaths() async {
    if (_hostPlatform == 'windows') {
      final localAppData = _environment['LOCALAPPDATA']?.trim() ?? '';
      return localAppData.isEmpty
          ? const []
          : [p.join(localAppData, 'Tomato Cake', 'launcher', 'Robotopia')];
    }
    if (_hostPlatform == 'macos') {
      final home = _environment['HOME']?.trim() ?? '';
      return home.isEmpty
          ? const []
          : [
              p.join(
                home,
                'Library',
                'Application Support',
                'Tomato Cake',
                'launcher',
              ),
            ];
    }
    return const [];
  }
}

abstract final class GameInstallDiscoverySources {
  static const savedSelection = GameInstallDiscoverySource(
    id: 'saved-selection',
    label: 'Saved selection',
    precedence: 0,
  );
  static const injectedPath = GameInstallDiscoverySource(
    id: 'injected-path',
    label: 'Configured path',
    precedence: -10,
  );
  static const environment = GameInstallDiscoverySource(
    id: 'environment',
    label: 'ROBOTOPIA_GAME_DIR',
    precedence: 20,
  );
  static const tomatoCake = GameInstallDiscoverySource(
    id: 'tomato-cake',
    label: 'Tomato Cake',
    precedence: 30,
  );
  static const steam = GameInstallDiscoverySource(
    id: 'steam',
    label: 'Steam',
    precedence: 40,
  );
}

/// Runs install discovery adapters and returns deterministic validated output.
class GameInstallDiscoveryService {
  GameInstallDiscoveryService({
    required Iterable<GameInstallDiscoveryAdapter> adapters,
    String? hostPlatform,
  }) : _adapters = List.unmodifiable(adapters),
       _hostPlatform = hostPlatform ?? Platform.operatingSystem;

  factory GameInstallDiscoveryService.standard({
    Map<String, String>? environment,
    String? hostPlatform,
    String? injectedPath,
    Iterable<String>? steamRoots,
  }) {
    final effectiveEnvironment = environment ?? Platform.environment;
    final effectivePlatform = hostPlatform ?? Platform.operatingSystem;
    return GameInstallDiscoveryService(
      hostPlatform: effectivePlatform,
      adapters: [
        if (injectedPath != null && injectedPath.trim().isNotEmpty)
          FixedGameInstallDiscoveryAdapter(
            path: injectedPath,
            source: GameInstallDiscoverySources.injectedPath,
          ),
        EnvironmentGameInstallDiscoveryAdapter(
          environment: effectiveEnvironment,
        ),
        TomatoCakeGameInstallDiscoveryAdapter(
          environment: effectiveEnvironment,
          hostPlatform: effectivePlatform,
        ),
        SteamGameInstallDiscoveryAdapter(
          environment: effectiveEnvironment,
          hostPlatform: effectivePlatform,
          steamRoots: steamRoots,
        ),
      ],
    );
  }

  final List<GameInstallDiscoveryAdapter> _adapters;
  final String _hostPlatform;

  Future<List<GameInstallCandidate>> discover({
    required GameInstallValidator validate,
    Iterable<GameInstallDiscoveryAdapter> additionalAdapters = const [],
  }) async {
    final adapters = [...additionalAdapters, ..._adapters]
      ..sort(_compareAdapters);
    final candidates = <String, _CandidateBuilder>{};

    for (final adapter in adapters) {
      List<String> rawPaths;
      try {
        rawPaths = await adapter.discoverPaths();
      } on Object {
        // A store client may be absent, locked, or mid-update. One adapter
        // must never prevent the remaining deterministic sources from running.
        continue;
      }
      final paths =
          rawPaths
              .map((path) => path.trim())
              .where((path) => path.isNotEmpty)
              .map((path) => p.normalize(Directory(path).absolute.path))
              .toSet()
              .toList()
            ..sort(_comparePaths);

      for (final path in paths) {
        final canonicalPath = await _canonicalExistingDirectory(path);
        if (canonicalPath == null) continue;
        GameInstall install;
        try {
          install = await validate(canonicalPath);
        } on Object {
          continue;
        }
        if (install.issues.any((issue) => issue.isBlocking)) {
          continue;
        }
        final key = _pathIdentity(install.path);
        final builder = candidates.putIfAbsent(
          key,
          () => _CandidateBuilder(install),
        );
        builder.sources[adapter.source.id] = adapter.source;
      }
    }

    final result = candidates.values.map((builder) {
      final sources = builder.sources.values.toList()..sort(_compareSources);
      return GameInstallCandidate(
        install: builder.install,
        sources: List.unmodifiable(sources),
      );
    }).toList();
    result.sort((left, right) {
      final byPrecedence = left.primarySource.precedence.compareTo(
        right.primarySource.precedence,
      );
      return byPrecedence != 0
          ? byPrecedence
          : _comparePaths(left.install.path, right.install.path);
    });
    return List.unmodifiable(result);
  }

  int _compareAdapters(
    GameInstallDiscoveryAdapter left,
    GameInstallDiscoveryAdapter right,
  ) => _compareSources(left.source, right.source);

  int _compareSources(
    GameInstallDiscoverySource left,
    GameInstallDiscoverySource right,
  ) {
    final byPrecedence = left.precedence.compareTo(right.precedence);
    return byPrecedence != 0 ? byPrecedence : left.id.compareTo(right.id);
  }

  int _comparePaths(String left, String right) {
    final canonicalLeft = _pathIdentity(left);
    final canonicalRight = _pathIdentity(right);
    final canonical = canonicalLeft.compareTo(canonicalRight);
    return canonical != 0 ? canonical : left.compareTo(right);
  }

  String _pathIdentity(String path) {
    final normalized = p.normalize(Directory(path).absolute.path);
    return _hostPlatform == 'windows' ? normalized.toLowerCase() : normalized;
  }

  Future<String?> _canonicalExistingDirectory(String path) async {
    try {
      final resolved = await Directory(path).resolveSymbolicLinks();
      if (FileSystemEntity.typeSync(resolved) !=
          FileSystemEntityType.directory) {
        return null;
      }
      return p.normalize(Directory(resolved).absolute.path);
    } on FileSystemException {
      return null;
    }
  }
}

class _CandidateBuilder {
  _CandidateBuilder(this.install);

  final GameInstall install;
  final Map<String, GameInstallDiscoverySource> sources = {};
}
