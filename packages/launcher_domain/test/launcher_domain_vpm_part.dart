part of 'launcher_domain_test.dart';

void _unityVpmResolverTests() {
  group('UnityVpmResolver', () {
    VpmListing listing() => VpmListing.fromJson(const {
      'name': 'Test',
      'id': 'test.repo',
      'packages': {
        'com.robotopia.ugc-companion': {
          'versions': {
            '0.1.0': {
              'name': 'com.robotopia.ugc-companion',
              'version': '0.1.0',
              'displayName': 'UGC Companion',
              'url': 'file:///companion-0.1.0.zip',
              'zipSHA256': 'sha-companion',
              'vpmDependencies': {'com.robotopia.vpm-resolver': '>=0.1.0'},
            },
            '0.2.0': {
              'name': 'com.robotopia.ugc-companion',
              'version': '0.2.0',
              'displayName': 'UGC Companion',
              'url': 'file:///companion-0.2.0.zip',
              'zipSHA256': 'sha-companion-2',
              'vpmDependencies': {'com.robotopia.vpm-resolver': '>=0.1.0'},
            },
          },
        },
        'com.robotopia.vpm-resolver': {
          'versions': {
            '0.1.0': {
              'name': 'com.robotopia.vpm-resolver',
              'version': '0.1.0',
              'url': 'file:///resolver-0.1.0.zip',
              'zipSHA256': 'sha-resolver',
            },
          },
        },
      },
    });

    test('resolves transitive deps in dependency-first order', () {
      final manifest = VpmManifest.fromJson(const {
        'dependencies': {'com.robotopia.ugc-companion': '>=0.1.0'},
      });
      final resolution = const UnityVpmResolver().resolve(
        manifest: manifest,
        catalog: listing(),
      );

      expect(resolution.hasBlockingIssues, isFalse);
      final ids = resolution.packages.map((p) => p.id).toList();
      expect(
        ids.indexOf('com.robotopia.vpm-resolver'),
        lessThan(ids.indexOf('com.robotopia.ugc-companion')),
      );

      final companion = resolution.packages.firstWhere(
        (p) => p.id == 'com.robotopia.ugc-companion',
      );
      expect(companion.version, '0.2.0');
      expect(companion.zipSha256, 'sha-companion-2');
    });

    test('reports a blocking issue for a missing/unsatisfiable package', () {
      final missing = const UnityVpmResolver().resolve(
        manifest: VpmManifest.fromJson(const {
          'dependencies': {'com.unknown.pkg': '^1.0.0'},
        }),
        catalog: listing(),
      );
      expect(missing.hasBlockingIssues, isTrue);

      final unsatisfiable = const UnityVpmResolver().resolve(
        manifest: VpmManifest.fromJson(const {
          'dependencies': {'com.robotopia.ugc-companion': '^9.0.0'},
        }),
        catalog: listing(),
      );
      expect(unsatisfiable.hasBlockingIssues, isTrue);
    });

    test(
      'vpmRangeAllows handles caret, tilde, wildcards, exact, comparators',
      () {
        expect(vpmRangeAllows('^0.1.0', '0.1.5'), isTrue);
        expect(vpmRangeAllows('^0.1.0', '0.2.0'), isFalse);
        expect(vpmRangeAllows('^1.2.0', '1.9.0'), isTrue);
        expect(vpmRangeAllows('^1.2.0', '2.0.0'), isFalse);
        expect(vpmRangeAllows('~1.2.3', '1.2.9'), isTrue);
        expect(vpmRangeAllows('~1.2.3', '1.3.0'), isFalse);
        expect(vpmRangeAllows('1.2.*', '1.2.7'), isTrue);
        expect(vpmRangeAllows('1.2.*', '1.3.0'), isFalse);
        expect(vpmRangeAllows('>=0.1.0', '5.0.0'), isTrue);
        expect(vpmRangeAllows('1.0.0', '1.0.0'), isTrue);
        expect(vpmRangeAllows('1.0.0', '1.0.1'), isFalse);
      },
    );

    test(
      'reports a conflict when two dependents require disjoint ranges of a shared dep',
      () {
        final catalog = VpmListing.fromJson(const {
          'packages': {
            'com.a': {
              'versions': {
                '1.0.0': {
                  'name': 'com.a',
                  'version': '1.0.0',
                  'url': 'file:///a.zip',
                  'vpmDependencies': {'com.shared': '^1.0.0'},
                },
              },
            },
            'com.c': {
              'versions': {
                '1.0.0': {
                  'name': 'com.c',
                  'version': '1.0.0',
                  'url': 'file:///c.zip',
                  'vpmDependencies': {'com.shared': '^2.0.0'},
                },
              },
            },
            'com.shared': {
              'versions': {
                '1.5.0': {
                  'name': 'com.shared',
                  'version': '1.5.0',
                  'url': 'file:///s1.zip',
                },
                '2.0.0': {
                  'name': 'com.shared',
                  'version': '2.0.0',
                  'url': 'file:///s2.zip',
                },
              },
            },
          },
        });
        final resolution = const UnityVpmResolver().resolve(
          manifest: VpmManifest.fromJson(const {
            'dependencies': {'com.a': '^1.0.0', 'com.c': '^1.0.0'},
          }),
          catalog: catalog,
        );

        expect(resolution.hasBlockingIssues, isTrue);
        expect(resolution.packages.any((p) => p.id == 'com.shared'), isFalse);
      },
    );

    test('VpmManifest round-trips dependencies + locked', () {
      const manifest = VpmManifest(
        dependencies: {'com.robotopia.ugc-companion': '^0.1.0'},
        locked: {
          'com.robotopia.ugc-companion': VpmLocked(
            version: '0.1.0',
            dependencies: {'com.robotopia.vpm-resolver': '>=0.1.0'},
          ),
        },
      );
      final back = VpmManifest.fromJson(manifest.toJson());

      expect(back.dependencies['com.robotopia.ugc-companion'], '^0.1.0');
      expect(back.locked['com.robotopia.ugc-companion']!.version, '0.1.0');
      expect(
        back
            .locked['com.robotopia.ugc-companion']!
            .dependencies['com.robotopia.vpm-resolver'],
        '>=0.1.0',
      );
    });
  });
}
