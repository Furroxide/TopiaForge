part of '../models.dart';

/// One source that identified a validated Robotopia installation.
///
/// Lower [precedence] values are preferred. The string [id] keeps the model
/// extensible so launcher_data can add store adapters without changing the
/// domain package.
class GameInstallDiscoverySource {
  const GameInstallDiscoverySource({
    required this.id,
    required this.label,
    required this.precedence,
  });

  final String id;
  final String label;
  final int precedence;
}

/// A launchable installation plus every source that independently found it.
///
/// Discovery services canonicalize and de-duplicate paths before constructing
/// this model. [sources] is ordered by precedence and then source id.
class GameInstallCandidate {
  const GameInstallCandidate({required this.install, required this.sources});

  static const _unknownSource = GameInstallDiscoverySource(
    id: 'unknown',
    label: 'Unknown source',
    precedence: 0x7fffffff,
  );

  final GameInstall install;
  final List<GameInstallDiscoverySource> sources;

  /// The preferred source. The fallback keeps consumers safe if a third-party
  /// repository mutates a previously valid source list after construction.
  GameInstallDiscoverySource get primarySource =>
      sources.isEmpty ? _unknownSource : sources.first;

  String get sourceSummary => sources.isEmpty
      ? _unknownSource.label
      : sources.map((source) => source.label).join(', ');
}
