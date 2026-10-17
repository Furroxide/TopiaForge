import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

/// The single-instance scope of the launcher data root [dataRoot]: 32
/// lowercase hexadecimal characters. The runner combines it with the
/// current user, so there is one launcher per user and data root, and two
/// installs with different data roots never take each other's links.
///
/// Spellings of one root share a scope: a relative root is resolved against
/// [currentDirectory], separators and `.` or `..` segments are normalized,
/// and on Windows, where paths ignore case, case is folded. Links and 8.3
/// short names are not resolved, so they count as other roots.
String launcherInstanceScope(
  String dataRoot, {
  bool? windows,
  String? currentDirectory,
}) {
  final onWindows = windows ?? Platform.isWindows;
  final context = p.Context(
    style: onWindows ? p.Style.windows : p.Style.posix,
    current: currentDirectory ?? Directory.current.path,
  );
  var root = context.normalize(context.absolute(dataRoot));
  if (onWindows) root = root.toLowerCase();
  final digest = sha256.convert(utf8.encode(root)).bytes;
  final buffer = StringBuffer();
  for (final value in digest.take(16)) {
    buffer.write(value.toRadixString(16).padLeft(2, '0'));
  }
  return buffer.toString();
}
