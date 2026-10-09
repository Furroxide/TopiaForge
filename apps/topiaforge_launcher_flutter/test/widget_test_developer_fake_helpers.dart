part of 'widget_test.dart';

extension _FakeDeveloperWorkspace on _FakeDeveloperRepository {
  DeveloperWorkspace _workspace() {
    return DeveloperWorkspace(
      projectRoot: '/tmp/creator',
      generatedPropsPath: '/tmp/creator/topiaforge.dev.props',
      project: hasProject
          ? DeveloperProject(
              schemaVersion: 2,
              id: 'creator.mod',
              name: 'Creator Mod',
              unityCompanion: const UnityCompanionSettings(),
            )
          : null,
      lock: hasProject
          ? const DeveloperLock(
              schemaVersion: 2,
              projectId: 'creator.mod',
              resolvedAtUtc: '2026-06-29T00:00:00Z',
              packages: [
                LockedPackage(
                  id: 'api.mod',
                  name: 'API Mod',
                  version: '1.0.0',
                  packageUrl: 'file:///api.topiaforgemod',
                  packageSha256: 'sha',
                  apiAssemblies: ['ref/Api.dll'],
                ),
              ],
            )
          : null,
    );
  }
}
