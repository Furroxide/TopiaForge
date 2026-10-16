/// Edits to the TopiaForge SDK `PackageReference` items of a C# project
/// file.
///
/// Mod scaffolding and `topiaforge mod add|remove` share these, so both find
/// and pin an item the same way. An item is found as [firstPackageReference]
/// finds it: by its id in any ASCII case, wherever `Include` sits in its
/// start tag, outside comments, and ending at its own `/>` or end tag. Each
/// package's edit takes time linear in the length of the project.
library;

import 'linear_text_scan.dart';
import 'msbuild_item_metadata.dart';
import 'sdk_reference_pack.dart';

/// [project] with the first `PackageReference` item for each of [packages]
/// pinned to exactly [version], and the missing items added in one
/// `ItemGroup` before `</Project>`.
///
/// An analyzer package also gets `PrivateAssets="all"` unless the item sets
/// PrivateAssets already. MSBuild applies the `Update` and `Remove` items
/// that follow a pinned item to it, so an `Update` item that names just its
/// package loses its Version metadata, and such a `Remove` item goes.
String withPinnedSdkPackageReferences(
  String project, {
  required Iterable<String> packages,
  required String version,
}) {
  var result = project;
  final missing = <String>[];
  for (final package in packages.toList()..sort()) {
    final reference = firstPackageReference(result, package);
    if (reference == null) {
      missing.add(package);
      continue;
    }
    final pinned = _pinned(
      result.substring(reference.start, reference.end),
      package: package,
      version: version,
    );
    result = _withoutOverrides(
      result.replaceRange(reference.start, reference.end, pinned),
      package,
      reference.start + pinned.length,
    );
  }
  if (missing.isEmpty) return result;
  final items = missing
      .map((package) {
        final private = topiaForgeAnalyzerPackageIds.contains(package)
            ? ' PrivateAssets="all"'
            : '';
        return '    <PackageReference Include="$package" Version="$version"$private />';
      })
      .join('\n');
  final close = result.lastIndexOf('</Project>');
  if (close < 0) throw StateError('TopiaForge C# project is malformed.');
  return '${result.substring(0, close)}  <ItemGroup>\n$items\n'
      '  </ItemGroup>\n${result.substring(close)}';
}

/// [project] without its first `PackageReference` item for [package], or
/// [project] itself when it has none.
String withoutPackageReference(String project, String package) {
  final reference = firstPackageReference(project, package);
  return reference == null
      ? project
      : project.replaceRange(reference.start, reference.end, '');
}

/// [item], the `PackageReference` item for [package], pinned to [version].
String _pinned(
  String item, {
  required String package,
  required String version,
}) {
  // MSBuild applies child metadata after the attributes, so a Version child
  // would override the pin. Dropping each one leaves the attribute as the
  // item's only version.
  var pinned = withoutMetadataElements(item, 'Version');
  // A missing attribute goes after the others, so an item written with
  // Include first, as the templates write it, keeps that order.
  pinned = withMetadataAttribute(pinned, 'Version', version);
  // PrivateAssets is added only when missing, and a child sets it as an
  // attribute does.
  if (topiaForgeAnalyzerPackageIds.contains(package) &&
      metadataAttributes(pinned, 'PrivateAssets').isEmpty &&
      metadataElements(pinned, 'PrivateAssets').isEmpty) {
    pinned = withMetadataAttribute(pinned, 'PrivateAssets', 'all');
  }
  return pinned;
}

/// [project] without the overrides of the pin on [package] that its items
/// from [index] on hold, [index] being where the pinned item ends.
///
/// MSBuild applies an `Update` or `Remove` item to the items before it,
/// even under a condition that is true, so a later one would undo the pin.
/// Each `Update` item that [namesPackage] loses its Version attributes and
/// children, as the pinned item loses its Version children, and keeps its
/// other metadata. Each such `Remove` item goes, with the white space before
/// it: the pin adds an SDK item that a project leaves out, and removing the
/// item is another way to leave it out. An item whose `Update` or `Remove`
/// names several ids or a pattern is left alone, because an edit would
/// change the other packages it names too.
String _withoutOverrides(String project, String package, int index) {
  final edited = StringBuffer();
  var from = 0;
  for (final item in packageReferences(project, start: index)) {
    // An item nested in one already edited is part of that edit.
    if (item.start < from) continue;
    var start = item.start;
    final String replacement;
    if (namesPackage(project, item.tag, 'Remove', package)) {
      while (start > from &&
          isRegExpWhitespace(project.codeUnitAt(start - 1))) {
        start--;
      }
      replacement = '';
    } else if (namesPackage(project, item.tag, 'Update', package)) {
      replacement = withoutMetadataAttributes(
        withoutMetadataElements(
          project.substring(item.start, item.end),
          'Version',
        ),
        'Version',
      );
    } else {
      continue;
    }
    edited
      ..write(project.substring(from, start))
      ..write(replacement);
    from = item.end;
  }
  // No item was edited.
  if (from == 0) return project;
  edited.write(project.substring(from));
  return edited.toString();
}
