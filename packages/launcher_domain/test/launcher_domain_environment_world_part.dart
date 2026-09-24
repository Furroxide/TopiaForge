part of 'launcher_domain_test.dart';

void _environmentAndWorldModelTests() {
  group('EnvironmentReport', () {
    test(
      'only develop-purpose tools block developing; optional ones do not',
      () {
        const env = EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.ok,
              purpose: ToolPurpose.develop,
            ),
            ToolCheck(
              name: 'Unity Editor',
              status: ToolStatus.missing,
              purpose: ToolPurpose.customWorldUnity,
            ),
            ToolCheck(
              name: 'Git',
              status: ToolStatus.warning,
              purpose: ToolPurpose.optional,
            ),
          ],
        );

        expect(env.developerReady, isTrue);
        expect(env.customWorldUnityReady, isFalse);
        expect(env.blockers, isEmpty);
      },
    );

    test('a missing develop tool is a blocker', () {
      const env = EnvironmentReport(
        checks: [
          ToolCheck(
            name: '.NET SDK',
            status: ToolStatus.missing,
            purpose: ToolPurpose.develop,
          ),
        ],
      );

      expect(env.developerReady, isFalse);
      expect(env.blockers, hasLength(1));
    });

    test('DeveloperSetupResult.ok mirrors environment.developerReady', () {
      const ready = DeveloperSetupResult(
        environment: EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.ok,
              purpose: ToolPurpose.develop,
            ),
          ],
        ),
        actions: ['Sidecar dependencies already present.'],
      );
      expect(ready.ok, isTrue);

      const notReady = DeveloperSetupResult(
        environment: EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.missing,
              purpose: ToolPurpose.develop,
            ),
          ],
        ),
      );
      expect(notReady.ok, isFalse);
    });
  });

  group('RobotopiaGameUnityCompatibility', () {
    const releaseEditor = UnityEditor(
      version: '6000.0.23f1',
      path: '/unity/6000.0.23f1',
    );
    const newestEditor = UnityEditor(
      version: '6000.2.10f1',
      path: '/unity/6000.2.10f1',
    );
    const configuredEditor = UnityEditor(
      version: '6000.0.31f1',
      path: '/unity/6000.0.31f1',
    );

    test('selects the exact release editor regardless of discovery order', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [
          newestEditor,
          releaseEditor,
        ]),
        same(releaseEditor),
      );
    });

    test('honors an explicit project editor pin', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [
          newestEditor,
          releaseEditor,
          configuredEditor,
        ], configuredVersion: ' 6000.0.31f1 '),
        same(configuredEditor),
      );
    });

    test('does not silently fall back to an incompatible editor', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [newestEditor]),
        isNull,
      );
    });
  });
}
