part of '../launch_resolution.dart';

/// A copied identity; no manifest or caller-owned collection can mutate it.
final class PackageIdentity {
  PackageIdentity({required String id, required String version})
    : id = _packageId(id),
      version = _version(version);
  factory PackageIdentity.fromJson(Object? value) {
    final json = _object(value, {'id', 'version'}, {'id', 'version'});
    return PackageIdentity(
      id: _packageId(json['id']),
      version: _version(json['version']),
    );
  }
  final String id;
  final String version;
  Map<String, Object?> toJson() => {'id': id, 'version': version};
}

/// Captures the installed manifest at construction, returning defensive copies.
final class ResolvedPackage extends PackageIdentity {
  ResolvedPackage({
    required super.id,
    required super.version,
    required ModManifest manifest,
  }) : _manifestJson = jsonEncode(manifest.toJson()) {
    if (!_idEquals(id, manifest.id) || version != manifest.version) {
      throw const FormatException(
        'Selected identity must agree with its manifest.',
      );
    }
  }
  final String _manifestJson;
  ModManifest get manifest =>
      ModManifest.fromJson(jsonDecode(_manifestJson) as Map<String, Object?>);
}

final class InstallFacts {
  const InstallFacts({
    this.platform = '',
    this.architecture = '',
    this.contentTarget = '',
    this.gameVersion = '',
  });
  final String platform;
  final String architecture;
  final String contentTarget;
  final String gameVersion;
}

/// Exact selected installed packages; duplicate selections remain diagnosable.
final class EffectiveProfile {
  EffectiveProfile({
    required Iterable<ResolvedPackage> packages,
    required String profileId,
    required int revision,
    this.install = const InstallFacts(),
    Iterable<ResolvedPackage> disabledPackages = const [],
  }) : profileId = _token(profileId),
       revision = _integer(revision),
       packages = _boundedCopy(packages),
       disabledPackages = _boundedCopy(disabledPackages);
  final String profileId;
  final int revision;
  final List<ResolvedPackage> packages;
  final List<ResolvedPackage> disabledPackages;
  final InstallFacts install;
}
