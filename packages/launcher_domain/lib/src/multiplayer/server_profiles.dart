part of '../multiplayer.dart';

/// The profiles joins save: one per server, `mp-<sha16(serverId)>`, named
/// `Server · <name>`, with every package pinned and nothing inherited from
/// the manager's mod state ([JoinPlan.derivedProfile]).
///
/// A join owns what decides what the profile loads and which server it is
/// for, and joining again replaces it:
///
/// - the id and the name, which follows the server's name;
/// - [LauncherProfile.inheritManagerModState], always off;
/// - the enabled packages and their version pins;
/// - Safe Mode, always off, since Safe Mode never joins a session.
///
/// Everything else on the profile is the player's and survives a re-join:
/// extra launch arguments, the launch environment, the remembered launch
/// selection, and the profile's config and backup metadata.
abstract final class ServerProfiles {
  static final _derivedId = RegExp(r'^mp-[0-9a-f]{16}$');

  /// Whether [profile] is a server profile: an id [JoinPlanner.derivedProfileId]
  /// could produce, and no manager mod state inherited.
  static bool isServerProfile(LauncherProfile profile) =>
      !profile.inheritManagerModState && _derivedId.hasMatch(profile.id);

  /// What a join saves for [derived], the plan's profile, when [existing] is
  /// the profile already saved under its id, if any. [existing] keeps its
  /// revision; the store counts the change.
  ///
  /// Throws [ArgumentError] when [derived] is not a server profile or names
  /// another id, and [StateError] when [existing] is not a server profile:
  /// a join never takes over a profile of the player's own.
  static LauncherProfile merge(
    LauncherProfile? existing,
    LauncherProfile derived,
  ) {
    if (!isServerProfile(derived)) {
      throw ArgumentError('Not a server profile.', 'derived');
    }
    if (existing == null) return derived;
    if (existing.id != derived.id) {
      throw ArgumentError('Another profile id.', 'existing');
    }
    if (!isServerProfile(existing)) {
      throw StateError(
        'A profile that is not a server profile already uses the id '
        '${derived.id}.',
      );
    }
    return LauncherProfile(
      id: derived.id,
      name: derived.name,
      inheritManagerModState: false,
      enabledMods: derived.enabledMods,
      selectedVersions: derived.selectedVersions,
      configMetadata: existing.configMetadata,
      launchSettings: existing.launchSettings.copyWith(safeMode: false),
      launchSelection: existing.launchSelection,
      revision: existing.revision,
      backupMetadata: existing.backupMetadata,
    );
  }
}
