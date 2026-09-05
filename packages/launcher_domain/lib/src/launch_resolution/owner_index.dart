part of '../launch_resolution.dart';

class _Owner {
  _Owner(this.id, this.enabled, this.disabled, this.ambiguous);
  final String id;
  final ResolvedPackage? enabled;
  final ResolvedPackage? disabled;
  final bool ambiguous;
}

/// Namespaces include disabled selections and missing declared dependencies.
/// A longer known owner remains authoritative even when it cannot supply content.
class _OwnerIndex {
  _OwnerIndex(this.profile) {
    final selected = <String, List<ResolvedPackage>>{};
    final namespaces = <String, String>{};
    for (final package in [...profile.packages, ...profile.disabledPackages]) {
      final key = package.id.toLowerCase();
      selected.putIfAbsent(key, () => []).add(package);
      namespaces[key] = package.id;
      final manifest = package.manifest;
      _manifests[package] = manifest;
      for (final dependency in manifest.allDependencies) {
        namespaces.putIfAbsent(
          dependency.id.toLowerCase(),
          () => dependency.id,
        );
      }
    }
    for (final entry in namespaces.entries) {
      final all = selected[entry.key] ?? const <ResolvedPackage>[];
      final enabled = all.where(profile.packages.contains).toList();
      final disabled = all.where(profile.disabledPackages.contains).toList();
      final packages = enabled.isNotEmpty ? enabled : disabled;
      final ids = packages.map((item) => item.id).toList()..sort();
      final ambiguous = packages.length > 1;
      if (ambiguous) {
        ambiguities.add(
          LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, ids.first),
        );
      }
      _owners.add(
        _Owner(
          ids.isEmpty ? entry.value : ids.first,
          enabled.firstOrNull,
          enabled.isEmpty ? disabled.firstOrNull : null,
          ambiguous,
        ),
      );
    }
    _owners.sort((a, b) {
      final length = b.id.length.compareTo(a.id.length);
      return length != 0 ? length : a.id.compareTo(b.id);
    });
  }
  final EffectiveProfile profile;
  final _manifests = <ResolvedPackage, ModManifest>{};
  final _owners = <_Owner>[];
  final ambiguities = <LaunchBlock>[];
  ModManifest manifest(ResolvedPackage package) => _manifests[package]!;
  _Owner? owner(String id, List<LaunchBlock> blocks) {
    final lower = id.toLowerCase();
    final match = _owners
        .where((owner) => lower.startsWith('${owner.id.toLowerCase()}.'))
        .firstOrNull;
    if (match?.ambiguous == true) {
      blocks.add(
        LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, match!.id),
      );
    }
    return match;
  }
}

void _checkReference(
  _OwnerIndex index,
  ResolvedPackage referrer,
  ResolvedPackage owner,
  String reference,
  LaunchBlockCode missingCode,
  List<LaunchBlock> blocks,
) {
  if (_idEquals(referrer.id, owner.id)) return;
  final dependencies = index
      .manifest(referrer)
      .dependencies
      .where((item) => _idEquals(item.id, owner.id));
  if (dependencies.isEmpty) {
    blocks.add(LaunchBlock(missingCode, reference, owner.version));
  } else if (!dependencies.every(
    (item) => item.versionRange.allows(owner.version),
  )) {
    blocks.add(
      LaunchBlock(
        LaunchBlockCode.targetPackageVersionUnsatisfied,
        reference,
        owner.version,
      ),
    );
  }
}

void _checkConsentReferences(
  _OwnerIndex index,
  _WorldMatch world,
  List<LaunchBlock> blocks,
) {
  for (final id in world.declaration.openTo) {
    final owner = index.owner(id, blocks);
    if (owner?.ambiguous == true) continue;
    final package = owner?.enabled;
    if (package == null) {
      final off = owner?.disabled;
      blocks.add(
        off == null
            ? LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, id)
            : LaunchBlock(
                LaunchBlockCode.gamemodePackageDisabled,
                off.id,
                off.version,
              ),
      );
      continue;
    }
    _checkReference(
      index,
      world.package,
      package,
      id,
      LaunchBlockCode.worldConsentRefNotADependency,
      blocks,
    );
    final declarations =
        index.manifest(package).contributions?.gamemodes ??
        const <ModGamemodeDeclaration>[];
    final matches = declarations
        .where((item) => _idEquals(item.id, id))
        .toList();
    if (matches.isEmpty) {
      blocks.add(LaunchBlock(LaunchBlockCode.gamemodeNotDeclared, id));
    }
    if (matches.length > 1) {
      blocks.add(LaunchBlock(LaunchBlockCode.declarationIdAmbiguous, id));
    }
  }
}
