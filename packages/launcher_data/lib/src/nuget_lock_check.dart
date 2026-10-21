/// What a NuGet restore resolved, as the lock file it wrote records it,
/// checked against the version every TopiaForge SDK package is pinned to.
///
/// The pin rewrites each SDK `PackageReference` in a mod project, but MSBuild
/// can still change what NuGet asks for from outside that item: a property or
/// item expression, an imported `.props` or `.targets` file such as
/// `Directory.Build.targets`, or a target. NuGet records the version it
/// resolved in the lock file, so reading that after a restore catches every
/// one of them.
library;

import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import 'linear_text_scan.dart';

/// Where NuGet put the lock file of a project, and how that was decided.
final class NuGetLockFileLocation {
  const NuGetLockFileLocation(this.file, this.source);

  final File file;
  final NuGetLockFileSource source;
}

/// How [nuGetLockFileOf] decided where a project's lock file is.
enum NuGetLockFileSource {
  /// `NuGetLockFilePath` as the restore recorded it in
  /// `obj/project.assets.json`.
  recorded,

  /// `NuGetLockFilePath` as the project file declares it in a plain
  /// `<PropertyGroup>` ([declaredNuGetLockFilePath]).
  declared,

  /// `packages.<project name>.lock.json` beside the project, which is there.
  projectNamed,

  /// `packages.lock.json` beside the project, NuGet's default.
  beside,
}

/// The lock file NuGet wrote for [project]. That is where `NuGetLockFilePath`
/// put it, as the restore recorded in `obj/project.assets.json`; otherwise
/// where [projectText], the project file's text, declares `NuGetLockFilePath`
/// in a plain `<PropertyGroup>`; otherwise `packages.<project name>.lock.json`
/// when one is there, which NuGet then uses instead; otherwise
/// `packages.lock.json` beside the project. Without [projectText], the
/// project file is read.
NuGetLockFileLocation nuGetLockFileOf(File project, {String? projectText}) {
  final folder = project.parent.path;
  final recorded = _recordedLockFilePath(
    File(p.join(folder, 'obj', 'project.assets.json')),
  );
  if (recorded != null) {
    return NuGetLockFileLocation(
      File(p.normalize(p.join(folder, recorded))),
      NuGetLockFileSource.recorded,
    );
  }
  final declared = declaredNuGetLockFilePath(
    projectText ?? _readSmallFile(project),
  );
  if (declared != null) {
    // MSBuild resolves the path against the project's folder, with either
    // separator.
    return NuGetLockFileLocation(
      File(p.normalize(p.join(folder, declared.replaceAll(r'\', '/')))),
      NuGetLockFileSource.declared,
    );
  }
  final named = File(p.join(folder, projectNamedNuGetLockFileName(project)));
  if (named.existsSync()) {
    return NuGetLockFileLocation(named, NuGetLockFileSource.projectNamed);
  }
  return NuGetLockFileLocation(
    File(p.join(folder, 'packages.lock.json')),
    NuGetLockFileSource.beside,
  );
}

/// `packages.<project name>.lock.json`, which NuGet uses for [project]
/// instead of `packages.lock.json` when it is there, with the spaces in the
/// project's name as underscores, as NuGet spells it.
String projectNamedNuGetLockFileName(File project) =>
    'packages.${p.basenameWithoutExtension(project.path).replaceAll(' ', '_')}'
    '.lock.json';

/// The `NuGetLockFilePath` that [project], the text of a project file,
/// declares in a plain `<PropertyGroup>`: one with no attributes, so with no
/// condition, outside any `<Target>` or `<Choose>`, holding a
/// `<NuGetLockFilePath>` element with no attributes and a literal path as
/// its text. The last such declaration wins, as it does in MSBuild. Null
/// when there is none, or when the last one holds an MSBuild expression,
/// which this does not evaluate. Comments and CDATA sections are skipped.
String? declaredNuGetLockFilePath(String project) {
  const name = 'NuGetLockFilePath';
  final conditional = _conditionalSpans(project);
  final groupEnds = _endTagOffsets(project, 'PropertyGroup');
  String? declared;
  for (final group in startTags(project, 'PropertyGroup')) {
    if (group.tag.attributes.isNotEmpty ||
        _isSelfClosing(project, group.tag) ||
        conditional.any(
          (span) => span.start < group.start && group.start < span.end,
        )) {
      continue;
    }
    final end = groupEnds.firstWhere(
      (offset) => offset >= group.tag.end,
      orElse: () => -1,
    );
    if (end < 0) break;
    final section = project.substring(group.tag.end, end);
    for (final element in startTags(section, name)) {
      if (element.tag.attributes.isNotEmpty ||
          _isSelfClosing(section, element.tag)) {
        continue;
      }
      final to = section.indexOf('<', element.tag.end);
      if (to < 0 || !section.startsWith('</$name>', to)) continue;
      final value = section.substring(element.tag.end, to).trim();
      declared = value.isEmpty || _holdsMsBuildExpression(value) ? null : value;
    }
  }
  return declared;
}

/// The spans of [project] inside `<Target>` and `<Choose>` elements, where
/// a `<PropertyGroup>` applies only when the target runs or a condition
/// holds. An element that does not close runs to the end of the text.
List<({int start, int end})> _conditionalSpans(String project) {
  final spans = <({int start, int end})>[];
  for (final name in const ['Target', 'Choose']) {
    final opens = [
      for (final tag in startTags(project, name))
        if (!_isSelfClosing(project, tag.tag)) tag.start,
    ];
    final closes = _endTagOffsets(project, name);
    // Walk the open and close tags in order, nesting elements of the same
    // name, as `<Choose>` may nest.
    var depth = 0;
    var start = 0;
    var open = 0;
    var close = 0;
    while (open < opens.length || (depth > 0 && close < closes.length)) {
      final closing =
          close < closes.length &&
          (open == opens.length || closes[close] < opens[open]);
      if (closing) {
        if (depth > 0) {
          depth--;
          if (depth == 0) spans.add((start: start, end: closes[close]));
        }
        close++;
      } else {
        if (depth == 0) start = opens[open];
        depth++;
        open++;
      }
    }
    if (depth > 0) spans.add((start: start, end: project.length));
  }
  return spans;
}

/// The offsets of the `</name>` end tags in [text], in order, outside
/// comments and CDATA sections. A section that does not close hides the
/// rest of [text].
List<int> _endTagOffsets(String text, String name) {
  final close = '</$name>';
  final offsets = <int>[];
  var open = text.indexOf('<');
  while (open >= 0) {
    final unparsedEnd = unparsedSectionEnd(text, open);
    if (unparsedEnd != null) {
      if (unparsedEnd < 0) break;
      open = text.indexOf('<', unparsedEnd);
      continue;
    }
    if (text.startsWith(close, open)) offsets.add(open);
    open = text.indexOf('<', open + 1);
  }
  return offsets;
}

bool _isSelfClosing(String text, StartTag tag) =>
    text.codeUnitAt(tag.end - 2) == 0x2F;

/// Whether [value] holds a property, item or metadata expression.
bool _holdsMsBuildExpression(String value) =>
    value.contains(r'$(') || value.contains('@(') || value.contains('%(');

/// The text of [file] when it is there and at most [maximumNuGetLockBytes],
/// or an empty string.
String _readSmallFile(File file) {
  try {
    if (!file.existsSync() || file.lengthSync() > maximumNuGetLockBytes) {
      return '';
    }
    return file.readAsStringSync();
  } on Object {
    return '';
  }
}

/// The `NuGetLockFilePath` a restore recorded in [assets], or null when it
/// recorded none or the file cannot be read.
String? _recordedLockFilePath(File assets) {
  try {
    if (!assets.existsSync() || assets.lengthSync() > _maximumAssetsBytes) {
      return null;
    }
    final json = jsonDecode(assets.readAsStringSync());
    final project = json is Map ? json['project'] : null;
    final restore = project is Map ? project['restore'] : null;
    final lock = restore is Map ? restore['restoreLockProperties'] : null;
    final path = lock is Map ? lock['nuGetLockFilePath'] : null;
    return path is String && path.trim().isNotEmpty ? path.trim() : null;
  } on Object {
    return null;
  }
}

/// Whether [lockFile] was written by the restore of its project that
/// started at [restoreStarted]. NuGet rewrites the lock file it uses on every
/// restore that `--force-evaluate` makes evaluate the project, in locked mode
/// too, so an older one is not the lock file that restore used: it is left
/// from an earlier restore, from before `NuGetLockFilePath`, or the `obj`
/// folder where a restore records it, moved. A file's time may read up to
/// [nuGetLockFileTimeTolerance] early, as FAT keeps times in two-second steps.
bool nuGetLockFileWrittenSince(File lockFile, DateTime restoreStarted) {
  try {
    return !lockFile.lastModifiedSync().isBefore(
      restoreStarted.subtract(nuGetLockFileTimeTolerance),
    );
  } on FileSystemException {
    return false;
  }
}

/// How much earlier than the restore that wrote it a lock file's time may
/// read.
const nuGetLockFileTimeTolerance = Duration(seconds: 2);

/// Far more than any mod project's assets file holds.
const _maximumAssetsBytes = 64 * 1024 * 1024;

/// The largest lock file read: far more than any mod project's.
const maximumNuGetLockBytes = 16 * 1024 * 1024;

/// An SDK package that a restore resolved at another version than the pin,
/// or not at all.
final class SdkPinMismatch {
  const SdkPinMismatch(this.packageId, this.resolved);

  /// The package id as the pin names it, or as the lock file spells it.
  final String packageId;

  /// What the lock file records instead, in the order it first appears:
  /// versions, or `a project reference`. Empty when no target resolved the
  /// package at all.
  final List<String> resolved;
}

/// The SDK packages [lock], a decoded NuGet lock file, resolved at another
/// version than [version], or did not resolve. Each target framework's
/// target in the lock file must resolve every package in [pinned] at
/// [version]. A runtime's target, such as `net10.0/win-x64`, lists only the
/// packages whose assets differ for that runtime, so it must resolve at
/// [version] only the pinned packages it lists. Any other package in
/// [sdkPackages] that a target resolves must be at [version] too, since the
/// SDK feed holds only that one. Ids compare without regard to case, as
/// NuGet compares them.
///
/// Throws [FormatException] when [lock] is not a NuGet lock file.
List<SdkPinMismatch> sdkPinMismatches(
  Object? lock, {
  required Set<String> pinned,
  required Set<String> sdkPackages,
  required String version,
}) {
  final dependencies = lock is Map ? lock['dependencies'] : null;
  if (dependencies is! Map) {
    throw const FormatException('It is not a NuGet lock file.');
  }
  final names = <String, String>{
    for (final id in sdkPackages) id.toLowerCase(): id,
    for (final id in pinned) id.toLowerCase(): id,
  };
  final found = <String, List<String>>{};
  final missing = <String>{};
  void record(String id, String resolved) {
    final versions = found.putIfAbsent(id, () => []);
    if (!versions.contains(resolved)) versions.add(resolved);
  }

  var frameworkTargets = 0;
  for (final MapEntry(key: name, value: target) in dependencies.entries) {
    if (target is! Map) {
      throw const FormatException('A target in it is not a list of packages.');
    }
    final seen = <String>{};
    for (final MapEntry(:key, :value) in target.entries) {
      final id = '$key'.toLowerCase();
      if (!names.containsKey(id)) continue;
      if (value is! Map) {
        throw const FormatException('A package in it has no description.');
      }
      seen.add(id);
      final resolved = value['resolved'];
      if (resolved is String) {
        if (resolved.toLowerCase() != version.toLowerCase()) {
          record(id, resolved);
        }
      } else if (value['type'] == 'Project') {
        record(id, 'a project reference');
      } else {
        throw const FormatException('A package in it has no resolved version.');
      }
    }
    // A runtime's target leaves out what resolves as it does for the target
    // framework alone, the SDK packages among it.
    if ('$name'.contains('/')) continue;
    frameworkTargets++;
    for (final id in pinned) {
      if (!seen.contains(id.toLowerCase())) missing.add(id.toLowerCase());
    }
  }
  if (frameworkTargets == 0) {
    missing.addAll(pinned.map((id) => id.toLowerCase()));
  }
  final ids = {...missing, ...found.keys}.toList()..sort();
  return [
    for (final id in ids) SdkPinMismatch(names[id]!, found[id] ?? const []),
  ];
}
