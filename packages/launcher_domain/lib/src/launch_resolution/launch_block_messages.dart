part of '../launch_resolution.dart';

extension LaunchBlockMessage on LaunchBlock {
  /// Shared actionable wording for launcher, CLI and diagnostics.
  String get message {
    final detail = switch (code) {
      LaunchBlockCode.targetNotDeclared =>
        'Install a package declaring this target, or choose another target',
      LaunchBlockCode.targetPlatformUnsupported =>
        'Choose a target compatible with this installation',
      LaunchBlockCode.gamemodePlatformUnsupported =>
        'Install a compatible gamemode',
      LaunchBlockCode.targetPackageDisabled =>
        'Enable the package that owns this target',
      LaunchBlockCode.targetPackageVersionUnsatisfied =>
        'Select a version satisfying the declared dependency',
      LaunchBlockCode.gamemodeNotDeclared =>
        'Install the package declaring this gamemode',
      LaunchBlockCode.gamemodePackageDisabled =>
        'Enable the package that owns this gamemode',
      LaunchBlockCode.gamemodeRefNotADependency =>
        'The target package must declare its gamemode dependency',
      LaunchBlockCode.gamemodeUnbound =>
        'Repair the gamemode package; its implementation is unavailable',
      LaunchBlockCode.worldNotDeclared =>
        'Install or discover this world, or choose another permitted world',
      LaunchBlockCode.worldPackageDisabled =>
        'Enable the package that owns this world',
      LaunchBlockCode.worldRefNotADependency =>
        'The target package must declare its referenced world dependency',
      LaunchBlockCode.worldNotAdmittedByPolicy =>
        'Choose a world permitted by this target',
      LaunchBlockCode.worldConsentMissing =>
        'Choose a world that consents to this gamemode',
      LaunchBlockCode.worldConsentRefNotADependency =>
        'The world package must declare its consent dependency',
      LaunchBlockCode.worldNotStaticallyDeclared =>
        'A default or allow list must name a static world',
      LaunchBlockCode.transitionUnsatisfiable =>
        'Choose a world with a compatible transition',
      LaunchBlockCode.transitionNotOffered =>
        'Choose a transition offered by this target and world',
      LaunchBlockCode.spawnRequirementUnsatisfied =>
        'Choose a world with the required authored spawn marker',
      LaunchBlockCode.worldPlatformUnsupported => 'Choose a compatible world',
      LaunchBlockCode.declarationIdAmbiguous =>
        'Repair duplicate package or declaration identities',
      LaunchBlockCode.noAvailableTarget =>
        'Repair or enable a permitted world before launching',
      LaunchBlockCode.planPackageSetMismatch =>
        'Refresh the profile; installed packages changed',
      LaunchBlockCode.planResolutionMismatch =>
        'Refresh the launch selection; its resolution changed',
      LaunchBlockCode.worldUnbound =>
        'Repair the world package; its provider is unavailable',
      LaunchBlockCode.worldUnavailable =>
        'Repair this world or choose another permitted world',
    };
    final identity = subjectVersion.isEmpty
        ? subject
        : '$subject@$subjectVersion';
    return identity.isEmpty ? '$detail.' : '$detail ($identity).';
  }
}
