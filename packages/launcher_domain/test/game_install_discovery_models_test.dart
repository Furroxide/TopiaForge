import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('candidate exposes ordered discovery provenance', () {
    const candidate = GameInstallCandidate(
      install: GameInstall(
        path: '/games/Robotopia',
        executablePath: '/games/Robotopia/Robotopia.exe',
        bepInExStatus: ComponentState.ready,
        loaderStatus: ComponentState.ready,
      ),
      sources: [
        GameInstallDiscoverySource(
          id: 'environment',
          label: 'ROBOTOPIA_GAME_DIR',
          precedence: 20,
        ),
        GameInstallDiscoverySource(id: 'steam', label: 'Steam', precedence: 40),
      ],
    );

    expect(candidate.primarySource.id, 'environment');
    expect(candidate.sourceSummary, 'ROBOTOPIA_GAME_DIR, Steam');
  });

  test('candidate degrades safely if external provenance is mutated', () {
    final sources = <GameInstallDiscoverySource>[
      const GameInstallDiscoverySource(
        id: 'custom',
        label: 'Custom store',
        precedence: 50,
      ),
    ];
    final candidate = GameInstallCandidate(
      install: const GameInstall(
        path: '/games/Robotopia',
        executablePath: '/games/Robotopia/Robotopia.exe',
        bepInExStatus: ComponentState.ready,
        loaderStatus: ComponentState.ready,
      ),
      sources: sources,
    );

    sources.clear();

    expect(candidate.primarySource.id, 'unknown');
    expect(candidate.sourceSummary, 'Unknown source');
  });
}
