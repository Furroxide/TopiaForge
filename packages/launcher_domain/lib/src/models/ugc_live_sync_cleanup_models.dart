part of '../models.dart';

class UgcLiveSyncCleanupReport {
  const UgcLiveSyncCleanupReport({
    required this.configPath,
    required this.commandPath,
    this.statusFileDeleted = false,
    this.sessionFileDeleted = false,
  });

  final String configPath;
  final String commandPath;
  final bool statusFileDeleted;
  final bool sessionFileDeleted;
}
