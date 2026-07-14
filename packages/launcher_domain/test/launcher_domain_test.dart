import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

part 'launcher_domain_environment_world_part.dart';
part 'launcher_domain_developer_part.dart';
part 'launcher_domain_vpm_part.dart';

void main() {
  group('ModManifest', () {
    test('parses extended clean manifest fields', () {
      final manifest = ModManifest.fromJson({
        'schemaVersion': 2,
        'name': 'author.spawn_tools',
        'displayName': 'Spawn Tools',
        'version': '1.2.0',
        'author': {'name': 'Author Name'},
        'entryAssembly': 'SpawnTools.dll',
        'entryType': 'SpawnTools.Entry',
        'vpmDependencies': {'robotopia.core': '>=1.0.0 <2.0.0'},
        'optionalDependencies': [
          {'id': 'robotopia.prompts', 'version': '1.0.0'},
        ],
        'conflicts': [
          {'id': 'legacy.prompt_patch', 'reason': 'Both override prompts.'},
        ],
        'supportedGameVersionRange': '>=0.8.0 <1.0.0',
        'supportedLoaderVersionRange': '>=0.1.0',
        'supportedSdkVersionRange': '>=0.1.0 <0.2.0',
        'category': 'Tools',
        'tags': ['sdk', 'assetbundle'],
        'license': 'MIT',
        'hashes': {'sha256': 'abc'},
        'apiAssemblies': ['ref/SpawnTools.Api.dll'],
        'legacyFolders': {'LegacySpawnTools': 'SpawnTools'},
        'legacyFiles': {'SpawnToolsLegacy.dll': 'SpawnTools.dll'},
        'legacyPackages': ['spawn-tools.zip'],
      });

      expect(manifest.validate(), isEmpty);
      expect(manifest.dependencies.single.versionRange.allows('1.5.0'), isTrue);
      expect(manifest.optionalDependencies.single.id, 'robotopia.prompts');
      expect(manifest.conflicts.single.id, 'legacy.prompt_patch');
      expect(manifest.tags, contains('assetbundle'));
      expect(manifest.hashes['sha256'], 'abc');
      expect(manifest.apiAssemblies.single, 'ref/SpawnTools.Api.dll');
      expect(manifest.legacyFiles.keys.single, 'SpawnToolsLegacy.dll');
      expect(manifest.toJson()['name'], 'author.spawn_tools');
      expect(manifest.toJson()['displayName'], 'Spawn Tools');
    });

    test(r'preserves $schema through a fromJson/toJson round-trip', () {
      final manifest = ModManifest.fromJson({
        r'$schema': ModManifest.canonicalSchemaUrl,
        'schemaVersion': 2,
        'name': 'author.schema_mod',
        'displayName': 'Schema Mod',
        'version': '1.0.0',
        'author': {'name': 'Author Name'},
        'entryAssembly': 'SchemaMod.dll',
        'entryType': 'SchemaMod.Entry',
      });

      final json = manifest.toJson();
      expect(json[r'$schema'], ModManifest.canonicalSchemaUrl);
      expect(json.keys.first, r'$schema');

      final withoutSchema = ModManifest.fromJson({
        'schemaVersion': 2,
        'name': 'author.schema_mod',
        'displayName': 'Schema Mod',
        'version': '1.0.0',
        'author': {'name': 'Author Name'},
        'entryAssembly': 'SchemaMod.dll',
        'entryType': 'SchemaMod.Entry',
      });
      expect(withoutSchema.toJson().containsKey(r'$schema'), isFalse);
    });

    test('rejects malformed manifests and unsafe entry paths', () {
      final manifest = ModManifest.fromJson({
        'schemaVersion': 1,
        'name': '../bad',
        'displayName': '',
        'version': 'nope',
        'author': {'name': ''},
        'entryAssembly': '../Bad.dll',
        'entryType': '',
      });

      final issues = manifest.validate();
      expect(issues.where((issue) => issue.isBlocking), hasLength(7));
    });

    test('warns but does not block on unknown permissions', () {
      final manifest = _manifest('permission.mod', permissions: ['new-scope']);

      final issues = manifest.validate();

      expect(issues.where((issue) => issue.isBlocking), isEmpty);
      expect(issues.single.message, contains('unknown value new-scope'));
    });

    test('accepts canonical descriptive capabilities', () {
      final manifest = _manifest(
        'capabilities.mod',
        permissions: const [
          'network',
          'remote-ai',
          'player-token',
          'microphone',
          'speech-to-text',
        ],
      );

      expect(manifest.validate(), isEmpty);
    });

    test('warns but does not block on non-SPDX-looking licenses', () {
      final manifest = _manifest('license.mod', license: 'free for streams');

      final issues = manifest.validate();

      expect(issues.where((issue) => issue.isBlocking), isEmpty);
      expect(issues.single.message, contains('SPDX-style identifier'));
    });
  });

  group('VersionRange', () {
    test('supports wildcard ranges for VPM-style package indexes', () {
      final range = VersionRange.parse('1.2.x');

      expect(range.allows('1.2.0'), isTrue);
      expect(range.allows('1.2.99'), isTrue);
      expect(range.allows('1.3.0'), isFalse);
    });
  });

  group('DependencyPlanner', () {
    test('orders dependencies before dependent mods', () {
      final dependency = _installed(_manifest('dependency.mod'));
      final main = _installed(
        _manifest(
          'main.mod',
          dependencies: [
            ModDependency(
              id: 'dependency.mod',
              versionRange: VersionRange(min: SemanticVersion(1, 0, 0)),
            ),
          ],
        ),
      );

      final result = const DependencyPlanner().resolveInstalled([
        main,
        dependency,
      ]);

      expect(result.hasBlockingIssues, isFalse);
      expect(result.orderedMods.map((mod) => mod.id), [
        'dependency.mod',
        'main.mod',
      ]);
    });

    test(
      'graph lists an id once when it is both a dependency and loadAfter',
      () {
        final dependency = _installed(_manifest('dependency.mod'));
        final main = _installed(
          _manifest(
            'main.mod',
            dependencies: [
              ModDependency(
                id: 'dependency.mod',
                versionRange: VersionRange(min: SemanticVersion(1, 0, 0)),
              ),
            ],
            loadAfter: ['dependency.mod'],
          ),
        );

        final result = const DependencyPlanner().resolveInstalled([
          main,
          dependency,
        ]);

        expect(result.graph['main.mod'], ['dependency.mod']);
      },
    );

    test('reports missing dependencies and conflicts before install', () {
      final installed = _installed(_manifest('old.prompt'));
      final candidate = _manifest(
        'new.prompt',
        dependencies: [const ModDependency(id: 'robotopia.core')],
        conflicts: [
          const ModConflict(id: 'old.prompt', reason: 'Prompt override clash.'),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(candidate, [
        installed,
      ]);

      expect(plan.hasBlockingIssues, isTrue);
      expect(plan.dependenciesToInstall.single.id, 'robotopia.core');
      expect(plan.conflictingMods.single.id, 'old.prompt');
    });

    test('plans registry dependencies before the root package', () {
      final dependency = RegistryMod(
        manifest: _manifest('robotopia.worlds', version: '1.0.0'),
        downloadUrl: 'file:///worlds.robotopiamod',
        packageSha256: 'abc',
        sourceName: 'Default',
      );
      final candidate = _manifest(
        'creator.mod',
        dependencies: [
          ModDependency(
            id: 'robotopia.worlds',
            versionRange: VersionRange(min: SemanticVersion(1, 0, 0)),
          ),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        packageUrl: 'file:///creator.robotopiamod',
        packageSha256: 'def',
        availableMods: [dependency],
      );

      expect(plan.hasBlockingIssues, isFalse);
      expect(plan.installActions.map((action) => action.modId), [
        'robotopia.worlds',
        'creator.mod',
      ]);
    });

    test('blocks remote package actions without SHA-256', () {
      final plan = const DependencyPlanner().previewInstall(
        _manifest('remote.mod'),
        const [],
        packageUrl: 'https://mods.example.com/remote.robotopiamod',
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(plan.issues.single.message, contains('SHA-256'));
    });
  });

  group('RegistryMod', () {
    test('reports update availability from installed version', () {
      final mod = RegistryMod(
        manifest: _manifest('timer.mod', version: '1.1.0'),
        installedVersion: '1.0.0',
      );
      final current = RegistryMod(
        manifest: _manifest('timer.mod', version: '1.1.0'),
        installedVersion: '1.1.0',
      );

      expect(mod.isInstalled, isTrue);
      expect(mod.updateAvailable, isTrue);
      expect(current.updateAvailable, isFalse);
    });
  });

  group('LauncherUpdateSettings', () {
    test('migrates self-update opt-ins to the manual-update state', () {
      const settings = LauncherUpdateSettings(
        enabled: true,
        checkAutomatically: false,
        channel: LauncherUpdateChannel.beta,
        manualReleasesUrl: 'https://updates.example.com/manual-releases.json',
      );

      final restored = LauncherUpdateSettings.fromJson(settings.toJson());

      expect(restored.enabled, isFalse);
      expect(restored.checkAutomatically, isFalse);
      expect(restored.channel, LauncherUpdateChannel.beta);
      expect(restored.manualReleasesUrl, settings.manualReleasesUrl);
    });

    test('launcher update settings reject plaintext and credential URLs', () {
      for (final unsafe in [
        'http://updates.example.com/manual-releases.json',
        'https://user:secret@updates.example.com/manual-releases.json',
        'file:///tmp/manual-releases.json',
        'https://updates.example.com/manual-releases.json?token=secret',
        'https://updates.example.com/manual-releases.json#latest',
        'https://updates.example.com/${List.filled(4100, 'a').join()}',
      ]) {
        final settings = LauncherUpdateSettings.fromJson({
          'manualReleasesUrl': unsafe,
        });
        expect(
          settings.manualReleasesUrl,
          LauncherUpdateSettings.defaultManualReleasesUrl,
        );
      }
    });

    test('validates the manual release catalog format 1 contract', () {
      final catalog = ManualReleaseCatalog.fromJson({
        'formatVersion': 1,
        'manualOnly': true,
        'releaseUrl': 'https://github.com/example/project/releases/tag/v1',
        'platforms': {
          'windows': {
            'url': 'https://downloads.example/launcher-windows.zip',
            'sha256': List.filled(64, 'a').join(),
            'size': 1024,
          },
        },
      });

      expect(catalog.isValid, isTrue);
      expect(catalog.toJson()['manualOnly'], isTrue);
      expect(
        ManualReleaseCatalog.fromJson({
          ...catalog.toJson(),
          'manualOnly': false,
        }).isValid,
        isFalse,
      );
    });

    test('treats stable as release channel for older settings', () {
      final settings = LauncherUpdateSettings.fromJson(const {
        'channel': 'stable',
      });

      expect(settings.channel, LauncherUpdateChannel.release);
      expect(settings.toJson()['channel'], 'release');
    });
  });

  group('LauncherProfile', () {
    test('round trips durable profile state', () {
      final profile = LauncherProfile(
        id: 'speedrun',
        name: 'Speedrun',
        enabledMods: {'timer.mod'},
        selectedVersions: {'timer.mod': '2.0.0'},
        launchSettings: const LaunchSettings(
          safeMode: true,
          extraArguments: ['-screen-fullscreen', '0'],
        ),
      );

      final restored = LauncherProfile.fromJson(profile.toJson());

      expect(restored.id, profile.id);
      expect(restored.enabledMods, contains('timer.mod'));
      expect(restored.selectedVersions['timer.mod'], '2.0.0');
      expect(restored.launchSettings.safeMode, isTrue);
    });

    test('round trips the world selection', () {
      const selection = WorldSelection(
        worldId: 'robotopia.level.city',
        gamemodeId: 'robotopia.zombies.survival',
        loadMode: WorldSelection.sceneReplacement,
        autoLoadOnStart: true,
      );
      final profile = LauncherProfile(
        id: 'p',
        name: 'P',
        worldSelection: selection,
      );

      final restored = LauncherProfile.fromJson(profile.toJson());

      expect(restored.worldSelection.worldId, selection.worldId);
      expect(restored.worldSelection.gamemodeId, selection.gamemodeId);
      expect(restored.worldSelection.loadMode, selection.loadMode);
      expect(restored.worldSelection.autoLoadOnStart, isTrue);
    });
  });

  _developerModelTests();

  _unityVpmResolverTests();
  _environmentAndWorldModelTests();
}

ModManifest _manifest(
  String id, {
  String version = '1.0.0',
  List<ModDependency> dependencies = const [],
  List<ModConflict> conflicts = const [],
  List<String> loadAfter = const [],
  List<String> apiAssemblies = const [],
  List<String> permissions = const [],
  String license = '',
}) {
  return ModManifest(
    schemaVersion: 2,
    id: id,
    name: id,
    version: version,
    author: const ModAuthor(name: 'QuantumWorks'),
    entryAssembly: '$id.dll',
    entryType: '$id.Entry',
    dependencies: dependencies,
    conflicts: conflicts,
    loadAfter: loadAfter,
    apiAssemblies: apiAssemblies,
    permissions: permissions,
    license: license,
  );
}

InstalledMod _installed(ModManifest manifest) {
  return InstalledMod(
    id: manifest.id,
    name: manifest.name,
    version: manifest.version,
    enabled: true,
    restartRequired: false,
    uninstallPending: false,
    packagePath: '/tmp/${manifest.id}',
    manifest: manifest,
  );
}
