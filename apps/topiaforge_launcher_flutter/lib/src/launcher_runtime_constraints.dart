part of 'launcher_bloc.dart';

String? _launcherGameArchitecture(GameInstall? install) {
  final architecture = install?.architecture ?? '';
  return architecture.isEmpty ? null : architecture;
}

String? _launcherGamePlatform(GameInstall? install) =>
    switch (install?.layout) {
      GameInstallLayout.macAppBundle => 'macos',
      GameInstallLayout.windowsNative ||
      GameInstallLayout.linuxProton => 'windows',
      null => null,
    };

List<String> _launcherGameContentTargets(GameInstall? install) =>
    switch (install?.layout) {
      GameInstallLayout.macAppBundle => const ['code', 'standaloneosx'],
      GameInstallLayout.windowsNative ||
      GameInstallLayout.linuxProton => const ['code', 'standalonewindows64'],
      null => const [],
    };

/// The installed mods the selected profile will actually load.
///
/// A profile either follows the global manager toggles or carries its own explicit set, and the two
/// differ often enough to matter: gating launch on the global set would block a profile that has
/// already excluded the offending mod. This mirrors the repository's own pre-launch effective-set
/// rule so the Launch button and the launch attempt reach the same verdict.
List<InstalledMod> _profileEffectiveMods(LauncherSnapshot snapshot) {
  final profile = _selectedSnapshotProfile(snapshot);
  if (profile == null || profile.inheritManagerModState) {
    return snapshot.installedMods;
  }

  final enabled = <String>{
    for (final id in profile.enabledMods) id.toLowerCase(),
  };
  return snapshot.installedMods
      .where((mod) => enabled.contains(mod.id.toLowerCase()))
      .toList(growable: false);
}

LauncherProfile? _selectedSnapshotProfile(LauncherSnapshot snapshot) {
  for (final profile in snapshot.profiles) {
    if (profile.id == snapshot.selectedProfileId) {
      return profile;
    }
  }
  return snapshot.profiles.isEmpty ? null : snapshot.profiles.first;
}
