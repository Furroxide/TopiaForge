import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

part 'dependency_planner_test_helpers.dart';

void main() {
  group('DependencyPlanner install activation', () {
    test('fails closed when a constrained package has no known game build', () {
      final constrained = _manifest(
        'constrained.mod',
        gameVersionRange: VersionRange.parse('>=0.0.2200 <0.0.2300'),
      );

      final strict = const DependencyPlanner().previewInstall(
        constrained,
        const [],
        requireKnownGameVersion: true,
      );
      final authoring = const DependencyPlanner().previewInstall(
        constrained,
        const [],
      );

      expect(strict.hasBlockingIssues, isTrue);
      expect(
        strict.issues.map((issue) => issue.message).join(' '),
        contains('installed-build.json could not be verified'),
      );
      expect(authoring.hasBlockingIssues, isFalse);
    });

    test('evaluates canonical game builds for root and transitive mods', () {
      final dependency = _manifest(
        'dependency.mod',
        gameVersionRange: VersionRange.parse('0.0.2227'),
      );
      final root = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'dependency.mod')],
        gameVersionRange: VersionRange.parse('>=0.0.2200 <0.0.2300'),
      );

      final compatible = const DependencyPlanner().previewInstall(
        root,
        const [],
        availableMods: [_registry(dependency)],
        gameVersion: '0.0.2227',
        requireKnownGameVersion: true,
      );
      final incompatible = const DependencyPlanner().previewInstall(
        root,
        const [],
        availableMods: [_registry(dependency)],
        gameVersion: '0.0.2300',
        requireKnownGameVersion: true,
      );

      expect(compatible.hasBlockingIssues, isFalse);
      expect(incompatible.hasBlockingIssues, isTrue);
    });

    test('plans a disabled direct dependency as enable-only', () {
      final dependency = _installed(
        _manifest('dependency.mod'),
        enabled: false,
      );
      final candidate = _manifest(
        'main.mod',
        dependencies: [
          ModDependency(
            id: 'dependency.mod',
            versionRange: VersionRange.parse('>=1.0.0'),
          ),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(candidate, [
        dependency,
      ]);

      expect(plan.hasBlockingIssues, isFalse);
      expect(plan.dependenciesToInstall, isEmpty);
      expect(plan.installActions.map((action) => action.modId), [
        'dependency.mod',
        'main.mod',
      ]);
      expect(plan.installActions.first.enableOnly, isTrue);
      expect(plan.installActions.last.root, isTrue);
    });

    test('plans disabled transitive dependencies before dependents', () {
      final base = _installed(_manifest('base.mod'), enabled: false);
      final feature = RegistryMod(
        manifest: _manifest(
          'feature.mod',
          dependencies: [
            ModDependency(
              id: 'base.mod',
              versionRange: VersionRange.parse('>=1.0.0'),
            ),
          ],
        ),
        downloadUrl: 'file:///feature.mod.robotopiamod',
        packageSha256: _validSha,
      );
      final candidate = _manifest(
        'main.mod',
        dependencies: [
          ModDependency(
            id: 'feature.mod',
            versionRange: VersionRange.parse('>=1.0.0'),
          ),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        [base],
        availableMods: [feature],
      );

      expect(plan.hasBlockingIssues, isFalse);
      expect(plan.installActions.map((action) => action.modId), [
        'base.mod',
        'feature.mod',
        'main.mod',
      ]);
      expect(plan.installActions[0].enableOnly, isTrue);
      expect(plan.installActions[1].enableOnly, isFalse);
    });

    test('repairs a disabled child of an already-satisfied dependency', () {
      final base = _installed(_manifest('base.mod'), enabled: false);
      final feature = _installed(
        _manifest(
          'feature.mod',
          dependencies: const [ModDependency(id: 'base.mod')],
        ),
      );
      final candidate = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'feature.mod')],
      );

      final plan = const DependencyPlanner().previewInstall(candidate, [
        feature,
        base,
      ]);

      expect(plan.hasBlockingIssues, isFalse);
      expect(plan.installActions.map((action) => action.modId), [
        'base.mod',
        'main.mod',
      ]);
      expect(plan.installActions.first.enableOnly, isTrue);
    });

    test('rejects transitive packages incompatible with the runtime', () {
      final candidate = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'feature.mod')],
      );
      final cases = [
        (
          manifest: _manifest(
            'feature.mod',
            gameVersionRange: VersionRange.parse('>=2.0.0'),
          ),
          game: '1.0.0',
          loader: RobotopiaRuntimeVersions.loaderVersion,
          sdk: RobotopiaRuntimeVersions.sdkVersion,
        ),
        (
          manifest: _manifest(
            'feature.mod',
            loaderVersionRange: VersionRange.parse('>=9.0.0'),
          ),
          game: '1.0.0',
          loader: RobotopiaRuntimeVersions.loaderVersion,
          sdk: RobotopiaRuntimeVersions.sdkVersion,
        ),
        (
          manifest: _manifest(
            'feature.mod',
            sdkVersionRange: VersionRange.parse('>=9.0.0'),
          ),
          game: '1.0.0',
          loader: RobotopiaRuntimeVersions.loaderVersion,
          sdk: RobotopiaRuntimeVersions.sdkVersion,
        ),
      ];

      for (final testCase in cases) {
        final plan = const DependencyPlanner().previewInstall(
          candidate,
          const [],
          availableMods: [_registry(testCase.manifest)],
          gameVersion: testCase.game,
          loaderVersion: testCase.loader,
          sdkVersion: testCase.sdk,
        );
        expect(plan.hasBlockingIssues, isTrue);
        expect(
          plan.installActions.where((action) => action.modId == 'feature.mod'),
          isEmpty,
        );
      }
    });

    test('blocks a conflict declared only by an installed mod', () {
      final installed = _installed(
        _manifest(
          'existing.mod',
          conflicts: const [ModConflict(id: 'main.mod')],
        ),
      );

      final plan = const DependencyPlanner().previewInstall(
        _manifest('main.mod'),
        [installed],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(plan.conflictingMods, contains(installed));
    });

    test('blocks conflicts between root and transitive packages', () {
      final feature = _manifest(
        'feature.mod',
        conflicts: const [ModConflict(id: 'main.mod')],
      );
      final candidate = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'feature.mod')],
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        availableMods: [_registry(feature)],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.issues.map((issue) => issue.message).join(' '),
        contains('Conflict between'),
      );
    });

    test('blocks conflicts between two transitive packages', () {
      final left = _manifest(
        'left.mod',
        conflicts: const [ModConflict(id: 'right.mod')],
      );
      final right = _manifest('right.mod');
      final candidate = _manifest(
        'main.mod',
        dependencies: const [
          ModDependency(id: 'left.mod'),
          ModDependency(id: 'right.mod'),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        availableMods: [_registry(left), _registry(right)],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.issues.map((issue) => issue.message).join(' '),
        allOf(contains('left.mod'), contains('right.mod')),
      );
    });

    test('blocks dependency cycles without duplicating the root action', () {
      final candidate = _manifest(
        'main.mod',
        dependencies: [
          ModDependency(
            id: 'feature.mod',
            versionRange: VersionRange.parse('>=1.0.0'),
          ),
        ],
      );
      final feature = RegistryMod(
        manifest: _manifest(
          'feature.mod',
          dependencies: [
            ModDependency(
              id: 'main.mod',
              versionRange: VersionRange.parse('>=1.0.0'),
            ),
          ],
        ),
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        availableMods: [
          feature,
          RegistryMod(manifest: candidate),
        ],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.issues.map((issue) => issue.message),
        contains(
          predicate(
            (message) => message.toString().contains('Dependency cycle'),
          ),
        ),
      );
      expect(plan.installActions.where((action) => action.root), hasLength(1));
      expect(plan.installActions.map((action) => action.modId), [
        'feature.mod',
        'main.mod',
      ]);
    });

    test('blocks malformed hashes on remote transitive packages', () {
      final candidate = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'feature.mod')],
      );
      final remote = RegistryMod(
        manifest: _manifest('feature.mod'),
        downloadUrl: 'https://packages.example/feature.robotopiamod',
        packageSha256: '../../escape',
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        availableMods: [remote],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.issues.map((issue) => issue.message).join(' '),
        contains('valid 64-digit SHA-256'),
      );
    });

    test('does not reactivate a dependency pending uninstall', () {
      final manifest = _manifest('dependency.mod');
      final dependency = InstalledMod(
        id: manifest.id,
        name: manifest.name,
        version: manifest.version,
        enabled: false,
        restartRequired: true,
        uninstallPending: true,
        packagePath: '/tmp/${manifest.id}',
        manifest: manifest,
      );
      final candidate = _manifest(
        'main.mod',
        dependencies: [
          ModDependency(
            id: manifest.id,
            versionRange: VersionRange.parse('>=1.0.0'),
          ),
        ],
      );

      final plan = const DependencyPlanner().previewInstall(candidate, [
        dependency,
      ]);

      expect(plan.hasBlockingIssues, isTrue);
      expect(plan.installActions.where((action) => action.enableOnly), isEmpty);
    });

    test('blocks incompatible transitive version constraints', () {
      final plan = const DependencyPlanner().previewInstall(
        _manifest(
          'main.mod',
          dependencies: const [
            ModDependency(id: 'a.mod'),
            ModDependency(id: 'b.mod'),
          ],
        ),
        const [],
        availableMods: [
          _registry(
            _manifest(
              'a.mod',
              dependencies: [
                ModDependency(
                  id: 'shared.mod',
                  versionRange: VersionRange.parse('<2.0.0'),
                ),
              ],
            ),
          ),
          _registry(
            _manifest(
              'b.mod',
              dependencies: [
                ModDependency(
                  id: 'shared.mod',
                  versionRange: VersionRange.parse('>=2.0.0'),
                ),
              ],
            ),
          ),
          _registry(_manifest('shared.mod', version: '1.5.0')),
          _registry(_manifest('shared.mod', version: '2.5.0')),
        ],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.issues.map((issue) => issue.message).join(' '),
        contains('shared.mod'),
      );
    });

    test('selects the newest version satisfying overlapping constraints', () {
      final plan = const DependencyPlanner().previewInstall(
        _manifest(
          'main.mod',
          dependencies: const [
            ModDependency(id: 'a.mod'),
            ModDependency(id: 'b.mod'),
          ],
        ),
        const [],
        availableMods: [
          _registry(
            _manifest(
              'a.mod',
              dependencies: [
                ModDependency(
                  id: 'shared.mod',
                  versionRange: VersionRange.parse('>=1.0.0 <3.0.0'),
                ),
              ],
            ),
          ),
          _registry(
            _manifest(
              'b.mod',
              dependencies: [
                ModDependency(
                  id: 'shared.mod',
                  versionRange: VersionRange.parse('<2.0.0'),
                ),
              ],
            ),
          ),
          _registry(_manifest('shared.mod', version: '1.5.0')),
          _registry(_manifest('shared.mod', version: '2.5.0')),
        ],
      );

      expect(plan.hasBlockingIssues, isFalse);
      final shared = plan.installActions.singleWhere(
        (action) => action.modId == 'shared.mod',
      );
      expect(shared.version, '1.5.0');
    });
  });
}
