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
  const DiagnosticBundle({
    required this.path,
    required this.createdAtUtc,
    required this.includedFiles,
  });

  final String path;
  final DateTime createdAtUtc;
  final List<String> includedFiles;
}
