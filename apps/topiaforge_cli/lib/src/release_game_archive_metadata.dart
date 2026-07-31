import 'release_policy.dart';

Map<String, Object?> selectTargetGameArchives(
  Map<String, Object?> gameMetadata,
  TopiaForgeReleasePolicy policy,
) {
  final archivesValue = gameMetadata['archives'];
  if (archivesValue is! Map) {
    throw StateError('Robotopia game-build archives must be an object.');
  }
  final archives = archivesValue.map(
    (key, value) => MapEntry(key.toString(), value),
  );
  final requiredArchiveIds = {
    for (final platform in policy.targetPlatforms)
      releasePlatformGameArchives[platform] ??
          (throw StateError(
            'No Robotopia game archive mapping exists for $platform.',
          )),
  };
  final selected = <String, Object?>{};
  for (final archiveId in requiredArchiveIds.toList()..sort()) {
    final archive = archives[archiveId];
    if (archive is! Map) {
      throw StateError('Robotopia game-build metadata omits $archiveId.');
    }
    selected[archiveId] = archive;
  }
  return selected;
}
