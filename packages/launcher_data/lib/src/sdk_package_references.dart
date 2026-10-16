/// Edits to the TopiaForge SDK `PackageReference` items of a C# project
/// file.
///
/// Mod scaffolding and `topiaforge mod add|remove` share these, so both find
/// and pin an item the same way. An item is found by its literal id, and ends
/// at its own `/>` or closing tag, as [firstPackageReference] bounds it. Each
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
/// PrivateAssets already.
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
    result = result.replaceRange(
      reference.start,
      reference.end,
      _pinned(
        result.substring(reference.start, reference.end),
        package: package,
        version: version,
      ),
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
  // A missing attribute goes after the others, so Include stays first and
  // the next rewrite finds this item rather than adding another.
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
