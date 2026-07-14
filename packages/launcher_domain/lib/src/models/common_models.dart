part of '../models.dart';

enum IssueSeverity { info, warning, error }

class LauncherIssue {
  const LauncherIssue({
    required this.severity,
    required this.message,
    this.subjectId,
  });

  final IssueSeverity severity;
  final String message;
  final String? subjectId;

  bool get isBlocking => severity == IssueSeverity.error;

  Map<String, Object?> toJson() => {
    'severity': severity.name,
    'message': message,
    if (subjectId != null) 'subjectId': subjectId,
  };
}

class DiagnosticBundle {
  DiagnosticBundle({
    required this.path,
    required this.createdAtUtc,
    required this.includedFiles,
    List<DiagnosticEntryMetadata> entries = const [],
  }) : entries = List.unmodifiable(entries);

  final String path;
  final DateTime createdAtUtc;
  final List<String> includedFiles;
  final List<DiagnosticEntryMetadata> entries;
}

class DiagnosticEntryMetadata {
  DiagnosticEntryMetadata({
    required this.name,
    required this.sha256,
    required this.sourceBytes,
    required this.includedBytes,
    this.truncated = false,
    List<String> truncationReasons = const [],
    this.byteLimit,
    this.lineLimit,
  }) : truncationReasons = List.unmodifiable(truncationReasons);

  final String name;
  final String sha256;
  final int sourceBytes;
  final int includedBytes;
  final bool truncated;
  final List<String> truncationReasons;
  final int? byteLimit;
  final int? lineLimit;

  Map<String, Object?> toJson() => {
    'name': name,
    'sha256': sha256,
    'sourceBytes': sourceBytes,
    'includedBytes': includedBytes,
    'truncated': truncated,
    if (truncationReasons.isNotEmpty) 'truncationReasons': truncationReasons,
    if (byteLimit != null) 'byteLimit': byteLimit,
    if (lineLimit != null) 'lineLimit': lineLimit,
  };
}
