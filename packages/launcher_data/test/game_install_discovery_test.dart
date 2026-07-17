import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  group('built-in path adapters', () {
    test('environment adapter returns only the explicit override', () async {
      final adapter = EnvironmentGameInstallDiscoveryAdapter(
        environment: const {
          'ROBOTOPIA_GAME_DIR': ' /games/Robotopia ',
          'LOCALAPPDATA': '/ignored',
        },
      );

      expect(await adapter.discoverPaths(), ['/games/Robotopia']);
    });

    test('Tomato Cake adapter uses documented platform paths', () async {
      final windows = TomatoCakeGameInstallDiscoveryAdapter(
        hostPlatform: 'windows',
        environment: const {'LOCALAPPDATA': r'C:\Users\Player\AppData\Local'},
      );
      final mac = TomatoCakeGameInstallDiscoveryAdapter(
        hostPlatform: 'macos',
        environment: const {'HOME': '/Users/player'},
      );
      final linux = TomatoCakeGameInstallDiscoveryAdapter(
        hostPlatform: 'linux',
        environment: const {'HOME': '/home/player'},
      );

      expect(await windows.discoverPaths(), [
        p.join(
          r'C:\Users\Player\AppData\Local',
          'Tomato Cake',
          'launcher',
          'Robotopia',
        ),
      ]);
      expect(await mac.discoverPaths(), [
        p.join(
          '/Users/player',
          'Library',
          'Application Support',
          'Tomato Cake',
          'launcher',
        ),
      ]);
      expect(await linux.discoverPaths(), isEmpty);
    });
  });

  test(
    'Steam adapter follows declared libraries and exact app metadata',
    () async {
      final root = Directory.systemTemp.createTempSync('topiaforge-steam-');
      addTearDown(() => root.deleteSync(recursive: true));
      final steamRoot = Directory(p.join(root.path, 'Steam'));
      final library = Directory(p.join(root.path, 'Library'));
      final rootApps = Directory(p.join(steamRoot.path, 'steamapps'))
        ..createSync(recursive: true);
      final libraryApps = Directory(p.join(library.path, 'steamapps'))
        ..createSync(recursive: true);
      File(p.join(rootApps.path, 'libraryfolders.vdf')).writeAsStringSync('''
"libraryfolders"
{
  "1"
  {
    "path" "${library.path}"
  }
}
''');
      File(p.join(rootApps.path, 'appmanifest_1.acf')).writeAsStringSync('''
"AppState" { "name" "Not Robotopia" "installdir" "Robotopia" }
''');
      File(
        p.join(libraryApps.path, 'appmanifest_987654.acf'),
      ).writeAsStringSync('''
"AppState"
{
  "name" "Robotopia"
  "installdir" "Robotopia"
}
''');
      File(p.join(libraryApps.path, 'appmanifest_2.acf')).writeAsStringSync('''
"AppState" { "name" "Robotopia" "installdir" "Robotopia Preview" }
''');

      final paths = await SteamGameInstallDiscoveryAdapter(
        hostPlatform: 'linux',
        environment: const {},
        steamRoots: [steamRoot.path],
      ).discoverPaths();

      expect(paths, [p.join(libraryApps.path, 'common', 'Robotopia')]);
    },
  );

  test(
    'Steam adapter rejects oversized metadata with a bounded read',
    () async {
      final root = Directory.systemTemp.createTempSync(
        'topiaforge-steam-large-',
      );
      addTearDown(() => root.deleteSync(recursive: true));
      final steamApps = Directory(p.join(root.path, 'steamapps'))
        ..createSync(recursive: true);
      File(p.join(steamApps.path, 'appmanifest_42.acf')).writeAsStringSync(
        '"name" "Robotopia" "installdir" "Robotopia" '
        '${List.filled(128, 'x').join()}',
      );

      final paths = await SteamGameInstallDiscoveryAdapter(
        environment: const {},
        steamRoots: [root.path],
        maxMetadataBytes: 64,
      ).discoverPaths();

      expect(paths, isEmpty);
    },
  );

  test('service validates, de-duplicates, and orders candidates', () async {
    final root = Directory.systemTemp.createTempSync('topiaforge-discovery-');
    addTearDown(() => root.deleteSync(recursive: true));
    final first = p.join(root.path, 'first');
    final second = p.join(root.path, 'second');
    final invalid = p.join(root.path, 'invalid');
    Directory(first).createSync();
    Directory(second).createSync();
    Directory(invalid).createSync();
    final canonicalFirst = Directory(first).resolveSymbolicLinksSync();
    final canonicalSecond = Directory(second).resolveSymbolicLinksSync();
    final canonicalInvalid = Directory(invalid).resolveSymbolicLinksSync();
    final service = GameInstallDiscoveryService(
      hostPlatform: 'linux',
      adapters: [
        _StaticAdapter(GameInstallDiscoverySources.steam, [
          second,
          first,
          invalid,
        ]),
        _StaticAdapter(GameInstallDiscoverySources.environment, [first]),
      ],
    );

    final candidates = await service.discover(
      validate: (path) async {
        final normalized = Directory(path).absolute.path;
        return GameInstall(
          path: normalized,
          executablePath: p.join(normalized, 'Robotopia.exe'),
          bepInExStatus: ComponentState.missing,
          loaderStatus: ComponentState.missing,
          issues: normalized == canonicalInvalid
              ? const [
                  LauncherIssue(
                    severity: IssueSeverity.error,
                    message: 'invalid',
                  ),
                ]
              : const [],
        );
      },
    );

    expect(candidates.map((candidate) => candidate.install.path), [
      canonicalFirst,
      canonicalSecond,
    ]);
    expect(candidates.first.sources.map((source) => source.id), [
      'environment',
      'steam',
    ]);
  });

  test(
    'service resolves symlink aliases to one canonical candidate',
    () async {
      final root = Directory.systemTemp.createTempSync('topiaforge-alias-');
      addTearDown(() => root.deleteSync(recursive: true));
      final installRoot = Directory(p.join(root.path, 'install'))..createSync();
      final alias = Link(p.join(root.path, 'alias'))
        ..createSync(installRoot.path);
      final service = GameInstallDiscoveryService(
        adapters: [
          _StaticAdapter(GameInstallDiscoverySources.environment, [alias.path]),
          _StaticAdapter(GameInstallDiscoverySources.steam, [installRoot.path]),
        ],
      );

      final candidates = await service.discover(
        validate: (path) async => GameInstall(
          path: path,
          executablePath: p.join(path, 'Robotopia.exe'),
          bepInExStatus: ComponentState.missing,
          loaderStatus: ComponentState.missing,
        ),
      );

      expect(candidates, hasLength(1));
      expect(
        candidates.single.install.path,
        installRoot.resolveSymbolicLinksSync(),
      );
      expect(candidates.single.sources.map((source) => source.id), [
        'environment',
        'steam',
      ]);
    },
    skip: Platform.isWindows
        ? 'Windows symlink creation needs privilege.'
        : false,
  );

  test('repository keeps the saved selection ahead of discoveries', () async {
    final root = Directory.systemTemp.createTempSync('topiaforge-selection-');
    addTearDown(() => root.deleteSync(recursive: true));
    final first = Directory(p.join(root.path, 'first'));
    final second = Directory(p.join(root.path, 'second'));
    _createGame(first);
    _createGame(second);
    final discovery = GameInstallDiscoveryService(
      adapters: [
        _StaticAdapter(GameInstallDiscoverySources.tomatoCake, [first.path]),
        _StaticAdapter(GameInstallDiscoverySources.steam, [second.path]),
      ],
    );
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: p.join(root.path, 'repo'),
      gameInstallDiscoveryService: discovery,
    );
    addTearDown(repository.dispose);
    final canonicalFirst = first.resolveSymbolicLinksSync();
    final canonicalSecond = second.resolveSymbolicLinksSync();

    expect(
      (await repository.discoverGameInstalls()).first.install.path,
      canonicalFirst,
    );
    await repository.selectGameDirectory(second.path);
    final afterSelection = await repository.discoverGameInstalls();

    expect(afterSelection.first.install.path, canonicalSecond);
    expect(afterSelection.first.primarySource.id, 'saved-selection');
    expect((await repository.detectKnownInstall())?.path, canonicalSecond);
  });

  test(
    'snapshot uses the canonical candidate for a saved symlink path',
    () async {
      final root = Directory.systemTemp.createTempSync(
        'topiaforge-saved-alias-',
      );
      addTearDown(() => root.deleteSync(recursive: true));
      final game = Directory(p.join(root.path, 'game'));
      _createGame(game);
      final alias = Link(p.join(root.path, 'game-alias'))
        ..createSync(game.path);
      final dataRoot = Directory(p.join(root.path, 'data'))..createSync();
      File(
        p.join(dataRoot.path, 'settings.json'),
      ).writeAsStringSync(jsonEncode({'gamePath': alias.path}));
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: p.join(root.path, 'repo'),
        gameInstallDiscoveryService: GameInstallDiscoveryService(
          adapters: [
            _StaticAdapter(GameInstallDiscoverySources.steam, [game.path]),
          ],
        ),
      );
      addTearDown(repository.dispose);
      await _disablePackageSources(repository, root);

      final snapshot = await repository.loadSnapshot();
      final canonical = game.resolveSymbolicLinksSync();

      expect(snapshot.gameInstall?.path, canonical);
      expect(snapshot.gameInstallCandidates.single.install.path, canonical);
      expect(
        snapshot.gameInstallCandidates.single.primarySource.id,
        'saved-selection',
      );
    },
    skip: Platform.isWindows
        ? 'Windows symlink creation needs privilege.'
        : false,
  );

  test('explicit known path bypasses a different saved selection', () async {
    final root = Directory.systemTemp.createTempSync('topiaforge-explicit-');
    addTearDown(() => root.deleteSync(recursive: true));
    final saved = Directory(p.join(root.path, 'saved'));
    final explicit = Directory(p.join(root.path, 'explicit'));
    _createGame(saved);
    _createGame(explicit);
    final dataRoot = p.join(root.path, 'data');
    final firstRepository = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: p.join(root.path, 'repo'),
      knownGamePath: saved.path,
    );
    await firstRepository.selectGameDirectory(saved.path);
    await firstRepository.dispose();
    final repository = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: p.join(root.path, 'repo'),
      knownGamePath: explicit.path,
    );
    addTearDown(repository.dispose);
    await _disablePackageSources(repository, root);
    final canonical = explicit.resolveSymbolicLinksSync();

    expect(
      (await repository.discoverGameInstalls()).first.install.path,
      canonical,
    );
    expect((await repository.detectKnownInstall())?.path, canonical);
    expect((await repository.loadSnapshot()).gameInstall?.path, canonical);
  });
}

class _StaticAdapter implements GameInstallDiscoveryAdapter {
  const _StaticAdapter(this.source, this.paths);

  @override
  final GameInstallDiscoverySource source;
  final List<String> paths;

  @override
  Future<List<String>> discoverPaths() async => paths;
}

void _createGame(Directory root) {
  File(p.join(root.path, 'Robotopia.exe')).createSync(recursive: true);
  File(
    p.join(root.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).createSync(recursive: true);
}

Future<void> _disablePackageSources(
  LocalLauncherRepository repository,
  Directory root,
) {
  return repository.savePackageSources([
    PackageSource(
      id: 'io.github.furroxide.topiaforge.local',
      name: 'Bundled Local Packages',
      url: Uri.file(p.join(root.path, 'dist')).toString(),
      enabled: false,
      builtIn: true,
    ),
    const PackageSource(
      id: ModRegistryFormat.officialSourceId,
      name: ModRegistryFormat.officialSourceName,
      url: ModRegistryFormat.officialRegistryUrl,
      enabled: false,
      builtIn: true,
    ),
  ]);
}
