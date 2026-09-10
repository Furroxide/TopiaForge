part of '../local_launcher_repository.dart';

extension _LaunchPreviews on LocalLauncherRepository {
  Future<LaunchPreview> _previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    final requested = selectionOverride ?? profile.launchSelection;
    try {
      _requireRuntimeDirectory(
        Directory(install.path),
        _managerRoot(install),
        label: 'Launch manager root',
      );
      _requireRuntimeDirectory(
        Directory(install.path),
        _managerStaging(install),
        label: 'Launch staging',
      );
      final refreshed = await _validateGameDirectory(install.path);
      _requireValidLauncherProfile(profile);
      final selection = await _effectiveLaunchSelection(refreshed, profile);
      final observations = await LaunchStagingStore(
        refreshed.path,
      ).readObservations();
      return LaunchPreviewBuilder.build(
        profile: selection.profile,
        installIdentity: _launchInstallIdentity(refreshed),
        requestedSelection: requested,
        safeMode: profile.launchSettings.safeMode,
        observation: RuntimeObservation.fromEnvelopes(
          selection.profile,
          observations,
        ),
        issues: [...refreshed.issues, ...selection.issues],
      );
    } on Object catch (error) {
      return LaunchPreview(
        profileId: profile.id,
        profileRevision: profile.revision,
        installIdentity: _launchInstallIdentity(install),
        packageDigest: packageSetDigest(const []),
        requestedSelection: requested,
        effectiveSelection: profile.launchSettings.safeMode
            ? const LaunchSelection.mainMenu()
            : requested,
        issues: [
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: profile.id,
            message: 'Launch selection could not be validated: $error',
          ),
        ],
      );
    }
  }

  String _launchInstallIdentity(GameInstall install) {
    final value = p.normalize(p.absolute(install.executablePath));
    return Platform.isWindows ? value.toLowerCase() : value;
  }
}
