/// Edits to the TopiaForge SDK `PackageReference` items of a C# project
/// file.
///
/// Mod scaffolding and `topiaforge mod add|remove` share these, so both find
/// and pin an item the same way. An item is found as [firstPackageReference]
/// finds it: by its id in any ASCII case, spelled with references or escapes
/// or not, wherever `Include` sits in its start tag, outside comments and
/// CDATA sections, and ending at its own `/>` or end tag. Each package's
/// edit takes time linear in the length of the project.
library;

import 'linear_text_scan.dart';
import 'msbuild_item_metadata.dart';
import 'msbuild_item_spec.dart';
import 'sdk_reference_pack.dart';

/// [project] with the first `PackageReference` item for each of [packages]
/// pinned to exactly [version], and the missing items added in one
/// `ItemGroup` before `</Project>`.
///
/// An analyzer package also gets `PrivateAssets="all"` unless the item sets
/// PrivateAssets already. MSBuild applies the `Update` and `Remove` items
/// that follow a pinned item to it, so an `Update` item that names just its
/// package loses its Version metadata, and such a `Remove` item goes.
///
/// Throws a [StateError] when an item that names a package together with
/// other packages would decide its version: the first item that includes
/// it, a later `Update` that sets a Version, or a later `Remove`. An edit to
/// such an item would change the other packages too.
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
    if (reference.shared) {
      throw StateError(
        '$package cannot be pinned to $version, because the PackageReference '
        'item ${_written(result, reference.start, 'Include')} names it '
        'together with other packages, and pinning that item would pin them '
        'too. Give $package an item of its own.',
      );
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
      version,
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
///
/// Throws a [StateError] when that item names other packages too, since
/// removing it would remove them as well.
String withoutPackageReference(String project, String package) {
  final reference = firstPackageReference(project, package);
  if (reference == null) return project;
  if (reference.shared) {
    throw StateError(
      '$package cannot be removed, because the PackageReference item '
      '${_written(project, reference.start, 'Include')} names it together '
      'with other packages, and removing that item would remove them too. '
      'Give $package an item of its own.',
    );
  }
  return project.replaceRange(reference.start, reference.end, '');
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
/// Each `Update` item that names just the package loses its Version
/// attributes and children, as the pinned item loses its Version children,
/// and keeps its other metadata. Each such `Remove` item goes, with the
/// white space before it: the pin adds an SDK item that a project leaves
/// out, and removing the item is another way to leave it out. An edit to
/// an item that names other packages too would change them, so such an
/// item that would undo the pin throws a [StateError] instead.
String _withoutOverrides(
  String project,
  String package,
  int index,
  String version,
) {
  final edited = StringBuffer();
  var from = 0;
  for (final item in packageReferences(project, start: index)) {
    // An item nested in one already edited is part of that edit.
    if (item.start < from) continue;
    final remove = packageNaming(project, item.tag, 'Remove', package);
    final update = packageNaming(project, item.tag, 'Update', package);
    if (remove == PackageNaming.shared ||
        update == PackageNaming.shared &&
            _setsVersion(project.substring(item.start, item.end))) {
      final removes = remove == PackageNaming.shared;
      final written = _written(
        project,
        item.start,
        removes ? 'Remove' : 'Update',
      );
      throw StateError(
        '$package cannot be pinned to $version, because the later '
        'PackageReference item $written names it in a list or by a wildcard '
        'and ${removes ? 'removes them' : 'sets their Version'}. Leave '
        '$package out of that item.',
      );
    }
    var start = item.start;
    final String replacement;
    if (remove == PackageNaming.alone) {
      while (start > from &&
          isRegExpWhitespace(project.codeUnitAt(start - 1))) {
        start--;
      }
      replacement = '';
    } else if (update == PackageNaming.alone) {
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

/// Whether the `PackageReference` [item] sets Version metadata, by an
/// attribute or a child in any case.
bool _setsVersion(String item) =>
    metadataAttributes(item, 'Version').isNotEmpty ||
    metadataElements(item, 'Version').isNotEmpty;

/// The first [attribute] of the start tag at [start] in [text], as written,
/// cut to 120 code units, to name the item in an error.
String _written(String text, int start, String attribute) {
  final tag = readStartTag(text, start)!;
  final found = tag.attributes.firstWhere(
    (candidate) => isAttributeNamed(text, candidate, attribute),
  );
  final written = text.substring(found.start, found.end);
  return written.length <= 120 ? written : '${written.substring(0, 117)}...';
}
