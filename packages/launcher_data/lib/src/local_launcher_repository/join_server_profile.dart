part of '../local_launcher_repository.dart';

extension _JoinServerProfile on LocalLauncherRepository {
  /// Saves a join plan's server profile, merged with the one an earlier join
  /// of the same server saved ([ServerProfiles.merge]): the plan's pins
  /// replace the old ones, and what the player set that the plan does not
  /// own stays. Every other profile, and which profile is selected, stay as
  /// they are. The profiles file is replaced in one step, so a save that
  /// fails leaves the earlier server profile intact.
  ///
  /// Once it is saved, the versions of server content no profile pins any
  /// more, such as the ones these pins replaced, are removed from [install]
  /// on their own; that never fails the join.
  Future<JoinEvent?> _prepareJoinProfile(
    GameInstall install,
    JoinSessionState state,
  ) async {
    final plan = state.plan;
    if (state.stage != JoinStage.preparingProfile || plan == null) {
      return null;
    }
    final derived = plan.derivedProfile;
    String? takenBy;
    try {
      await _profileMutation(() async {
        final (stored, _) = await _readProfileStore();
        final profiles = stored.isEmpty
            ? [LauncherProfile.defaultProfile()]
            : [...stored];
        final index = profiles.indexWhere(
          (profile) => profile.id == derived.id,
        );
        final existing = index < 0 ? null : profiles[index];
        if (existing != null && !ServerProfiles.isServerProfile(existing)) {
          takenBy = existing.name;
          return;
        }
        final merged = _requireValidLauncherProfile(
          ServerProfiles.merge(existing, derived),
        );
        if (existing == null) {
          profiles.add(merged.copyWith(revision: 0));
        } else {
          final changed =
              _canonicalProfileValue(existing.toJson()..remove('revision')) !=
              _canonicalProfileValue(merged.toJson()..remove('revision'));
          if (!changed) return;
          if (existing.revision == 2147483647) {
            throw StateError('The server profile revision limit is reached.');
          }
          profiles[index] = merged.copyWith(revision: existing.revision + 1);
        }
        await _writeProfiles(profiles);
      });
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Saving server profile ${derived.id} failed: $error',
      );
      return JoinFailed(
        JoinFailureCode.profileSaveFailed,
        detail:
            "The server's profile could not be saved, so your profiles are as "
            "they were. Check that the launcher's data folder can be written "
            'to, then join again.',
      );
    }
    if (takenBy != null) {
      return JoinFailed(
        JoinFailureCode.profileSaveFailed,
        detail:
            'Your profile "$takenBy" takes the place this server\'s profile '
            'needs, so it was left as it is. Duplicate it in Profiles, remove '
            'the original, then join again.',
      );
    }
    await _appendLauncherLogBestEffort('Saved server profile ${derived.id}.');
    await _removeUnpinnedServerContent(install);
    return const JoinProfilePrepared();
  }
}
