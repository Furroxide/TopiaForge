part of '../multiplayer.dart';

/// What the launcher found for one descriptor package, looked up by its exact
/// SHA-256 in the installed packages and the player's registry sources.
enum JoinPackageAvailability {
  /// The exact archive is installed; its install receipt supplies the
  /// manifest.
  installed,

  /// An enabled source offers the exact archive; the source's index supplies
  /// the manifest.
  available,

  /// Only a disabled source offers the exact archive.
  inDisabledSource,

  /// This id and version are installed as another archive. Both would need
  /// the same folder, and a join never replaces an installed archive.
  installedAsOtherArchive,

  /// A source offers this id and version, but as a different archive.
  hashMismatch,

  /// Sources know the mod, but not this version.
  versionUnavailable,

  /// No enabled source knows the mod.
  notInSources,

  /// Developer mode only: the host serves the archive itself. Its manifest is
  /// unknown until the archive is downloaded and checked.
  hostOffered,
}

/// The lookup result for one descriptor package. Only [installed] and
/// [available] results carry a manifest, and only those manifests are
/// trusted: they come from the player's own install receipts and sources,
/// never from the host.
final class JoinPackageResolution {
  const JoinPackageResolution._({
    required this.availability,
    this.manifest,
    this.sourceId = '',
    this.sourceName = '',
    this.downloadUrl = '',
    this.sizeBytes,
  });

  /// The exact archive is installed, described by its receipt's [manifest].
  factory JoinPackageResolution.installed(ModManifest manifest) =>
      JoinPackageResolution._(
        availability: JoinPackageAvailability.installed,
        manifest: manifest,
      );

  /// An enabled source offers the exact archive at [downloadUrl].
  factory JoinPackageResolution.available({
    required ModManifest manifest,
    required String sourceId,
    required String sourceName,
    required String downloadUrl,
    int? sizeBytes,
  }) {
    if (downloadUrl.trim().isEmpty) {
      throw ArgumentError('Required.', 'downloadUrl');
    }
    _requireSize(sizeBytes);
    return JoinPackageResolution._(
      availability: JoinPackageAvailability.available,
      manifest: manifest,
      sourceId: sourceId,
      sourceName: sourceName,
      downloadUrl: downloadUrl,
      sizeBytes: sizeBytes,
    );
  }

  /// No usable copy: [reason] is [JoinPackageAvailability.inDisabledSource],
  /// [JoinPackageAvailability.installedAsOtherArchive],
  /// [JoinPackageAvailability.hashMismatch],
  /// [JoinPackageAvailability.versionUnavailable] or
  /// [JoinPackageAvailability.notInSources]. [sourceName] names the source
  /// involved, when one is.
  factory JoinPackageResolution.unavailable(
    JoinPackageAvailability reason, {
    String sourceName = '',
  }) {
    if (reason == JoinPackageAvailability.installed ||
        reason == JoinPackageAvailability.available ||
        reason == JoinPackageAvailability.hostOffered) {
      throw ArgumentError.value(reason, 'reason', 'Not an unavailability.');
    }
    return JoinPackageResolution._(
      availability: reason,
      sourceName: sourceName,
    );
  }

  /// Developer mode: the host offers the archive.
  factory JoinPackageResolution.hostOffered({int? sizeBytes}) {
    _requireSize(sizeBytes);
    return JoinPackageResolution._(
      availability: JoinPackageAvailability.hostOffered,
      sizeBytes: sizeBytes,
    );
  }

  final JoinPackageAvailability availability;

  /// The trusted manifest of an installed or available archive.
  final ModManifest? manifest;
  final String sourceId;
  final String sourceName;
  final String downloadUrl;

  /// The archive size in bytes, when the source or host stated it.
  final int? sizeBytes;

  static void _requireSize(int? sizeBytes) {
    if (sizeBytes != null &&
        (sizeBytes < 0 || sizeBytes > ModRegistryFormat.maxPackageBytes)) {
      throw RangeError.range(
        sizeBytes,
        0,
        ModRegistryFormat.maxPackageBytes,
        'sizeBytes',
      );
    }
  }
}

/// The installed game and runtime a join plan is made for.
final class JoinEnvironment {
  JoinEnvironment({
    required this.gameBuild,
    required this.runtimeVersion,
    required this.sdkVersion,
    required this.protocolVersion,
    this.protocolPeerVersionRange,
    this.platform,
    this.architecture,
    this.developerMode = false,
  }) {
    for (final (field, value) in [
      ('gameBuild', gameBuild),
      ('runtimeVersion', runtimeVersion),
      ('sdkVersion', sdkVersion),
      ('protocolVersion', protocolVersion),
    ]) {
      if (value.trim().isEmpty) {
        throw ArgumentError('Required.', field);
      }
    }
  }

  /// The installed Robotopia build, as a build id (`2545`) or its version
  /// form (`0.0.2545`).
  final String gameBuild;

  /// The installed TopiaForge runtime (loader) version.
  final String runtimeVersion;

  /// The installed TopiaForge SDK version.
  final String sdkVersion;

  /// The multiplayer protocol version the installed runtime speaks.
  final String protocolVersion;

  /// The protocol versions the installed runtime accepts from peers, when it
  /// declares a range rather than exact equality.
  final String? protocolPeerVersionRange;
  final String? platform;
  final String? architecture;

  /// Developer mode allows host-served packages, flagged for consent.
  final bool developerMode;
}
