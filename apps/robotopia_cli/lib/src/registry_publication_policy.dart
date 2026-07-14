part of 'mod_registry_index_builder.dart';

List<LauncherIssue> validateRegistryPublicationHistory({
  required String entriesDirectory,
  required String previousEntriesDirectory,
}) {
  if (previousEntriesDirectory.trim().isEmpty) return const [];
  final current = _publicationEntryFiles(entriesDirectory);
  final previous = _publicationEntryFiles(previousEntriesDirectory);
  final issues = <LauncherIssue>[];
  for (final prior in previous.entries) {
    final next = current[prior.key];
    if (next == null) {
      issues.add(
        _historyIssue(
          'Published registry entry ${prior.key} was deleted or renamed; historical files are immutable.',
        ),
      );
      continue;
    }
    final priorEntry = _publicationEntry(prior.value);
    final nextEntry = _publicationEntry(next);
    if (priorEntry.id != nextEntry.id) {
      issues.add(
        _historyIssue(
          '${prior.value.path} changed id ${priorEntry.id} to ${nextEntry.id}; compare with ${next.path}.',
        ),
      );
      continue;
    }
    final oldVersions = priorEntry.versions;
    final newVersions = nextEntry.versions;
    if (newVersions.length < oldVersions.length) {
      issues.add(
        _historyIssue(
          '${prior.value.path} lost a published historical version in ${next.path}.',
        ),
      );
      continue;
    }
    final added = newVersions.length - oldVersions.length;
    for (var index = 0; index < oldVersions.length; index += 1) {
      final oldJson = oldVersions[index].toJson();
      final newJson = newVersions[added + index].toJson();
      if (jsonEncode(oldJson) != jsonEncode(newJson)) {
        issues.add(
          _historyIssue(
            '${next.path} changed or reordered published ${priorEntry.id}@${oldVersions[index].version}; original source: ${prior.value.path}.',
          ),
        );
      }
    }
    SemanticVersion? newestOld = oldVersions.isEmpty
        ? null
        : SemanticVersion.tryParse(oldVersions.first.version);
    final seen = <String>{for (final version in oldVersions) version.version};
    for (var index = added - 1; index >= 0; index -= 1) {
      final version = newVersions[index];
      final parsed = SemanticVersion.tryParse(version.version);
      if (parsed == null ||
          !seen.add(version.version) ||
          (newestOld != null && parsed.compareTo(newestOld) <= 0)) {
        issues.add(
          _historyIssue(
            '${next.path} may only prepend unique versions strictly newer than published history in ${prior.value.path}.',
          ),
        );
      } else {
        newestOld = parsed;
      }
    }
  }
  return issues;
}

Map<String, File> _publicationEntryFiles(String directoryPath) {
  final directory = Directory(directoryPath);
  if (!directory.existsSync()) return const {};
  final result = <String, File>{};
  for (final file in listBoundedDirectorySync(directory).whereType<File>()) {
    final name = p.basename(file.path).toLowerCase();
    if (name.endsWith('.json') && name != 'readme.json') result[name] = file;
  }
  return result;
}

RegistryEntryFile _publicationEntry(File file) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file ||
      file.lengthSync() <= 0 ||
      file.lengthSync() > 4 * 1024 * 1024) {
    throw StateError(
      'Registry history source is not a bounded regular file: ${file.path}',
    );
  }
  return RegistryEntryFile.fromJson(
    readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.registryEntry),
  );
}

LauncherIssue _historyIssue(String message) =>
    LauncherIssue(severity: IssueSeverity.error, message: message);
