part of 'widget_test.dart';

extension _FakeDeveloperWorkspace on _FakeDeveloperRepository {
  DeveloperWorkspace _workspace([UgcLiveSyncSettings? settings]) {
    final liveSync = settings ?? initialUgcSettings;
    return DeveloperWorkspace(
      projectRoot: '/tmp/creator',
      generatedPropsPath: '/tmp/creator/robotopia.dev.props',
      project: hasProject
          ? DeveloperProject(
              schemaVersion: 1,
              id: 'creator.mod',
              name: 'Creator Mod',
              unityCompanion: UnityCompanionSettings(liveSync: liveSync),
            )
          : null,
      lock: hasProject
          ? const DeveloperLock(
              schemaVersion: 1,
              projectId: 'creator.mod',
              resolvedAtUtc: '2026-06-29T00:00:00Z',
              packages: [
                LockedPackage(
                  id: 'api.mod',
                  name: 'API Mod',
                  version: '1.0.0',
                  packageUrl: 'file:///api.robotopiamod',
                  packageSha256: 'sha',
                  apiAssemblies: ['ref/Api.dll'],
                ),
              ],
            )
          : null,
    );
  }
}
