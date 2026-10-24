/// Where a join's host-served packages come from in developer mode: the
/// host's own descriptor endpoint, which answers `TFD1 package <sha256>`
/// (`docs/internal/MultiplayerLiveSessions.md`, section 15).
///
/// The data layer implements [JoinHostPackageSource]. The multiplayer facade
/// makes one for each downloading stage it answers and hands it to
/// [MultiplayerContentHost.downloadJoinContent], which keeps the launcher's
/// own rules: developer mode, the player's acknowledgement, the pinned
/// SHA-256 and the manifest. Nothing here names an address.
library;

import 'cancellation.dart';
import 'multiplayer.dart';
import 'repositories.dart';

/// Why a package the host serves was not downloaded.
enum JoinHostPackageProblem {
  /// The join reaches its host only through the TopiaForge service or its
  /// relay, so the launcher does not know where the host is. A host shares
  /// packages only over a direct or local-network connection.
  hostUnknown,

  /// Reaching the host now would show it the player's address, which the
  /// session keeps hidden.
  directNotAllowed,

  /// The host is no longer heard on the local network: its beacons stopped,
  /// or LAN discovery is off.
  notHeard,

  /// The connection could not be made, or failed.
  unreachable,

  /// The host stopped sending part way and stayed silent past the deadline.
  stalled,

  /// The transfer took longer than a host gives one.
  timedOut,

  /// `TFD1 404`: the host does not have the archive, or could not open it.
  notServed,

  /// `TFD1 429` or `TFD1 503`: the host is limiting this player's requests,
  /// or has as many connections open as it takes.
  busy,

  /// Any other status, such as `TFD1 400`.
  refused,

  /// The answer broke the protocol: no status line, or not a TFD1 one.
  malformed,

  /// The announced length is larger than a mod package may be.
  tooLarge,

  /// The connection ended before the announced length.
  endedEarly,

  /// The archive could not be written to disk.
  notSaved,

  /// The download was cancelled.
  cancelled,
}

/// What [JoinHostPackageSource.download] did.
sealed class JoinHostPackageResult {
  const JoinHostPackageResult();
}

/// The announced body arrived whole and was written to the file asked for.
/// Whether it is the archive the join pins is the caller's check.
final class JoinHostPackageDownloaded extends JoinHostPackageResult {
  const JoinHostPackageDownloaded({required this.sha256, required this.length});

  /// The lowercase hexadecimal SHA-256 of what was written, computed as it
  /// arrived.
  final String sha256;
  final int length;
}

/// Nothing was kept: the file asked for is not there.
final class JoinHostPackageFailed extends JoinHostPackageResult {
  const JoinHostPackageFailed(this.problem);

  final JoinHostPackageProblem problem;

  @override
  String toString() => 'host package download failed (${problem.name})';
}

/// The packages one join takes from its host, reached under the join's
/// [JoinContactRules], which are checked again before every package: the
/// host is reached only as the descriptor fetch reached it, never for a join
/// the service resolves, and under relay-only never beyond the local
/// network. A LAN listing's host is wherever its server's beacons come from
/// when each package is fetched; a direct link's or typed address's is only
/// the address that served the descriptor the join verified.
abstract interface class JoinHostPackageSource {
  /// Readies the host for downloads, before anything is fetched: null when
  /// it may be asked for packages, otherwise why not. A host heard on the
  /// local network is kept heard until the facade that made this source
  /// releases it, after the downloading stage. Never throws.
  Future<JoinHostPackageProblem?> prepare({CancellationToken? cancellation});

  /// Streams the archive of [package] into a new file at [path], computing
  /// its SHA-256 as it arrives, bounded by `ModRegistryFormat.maxPackageBytes`
  /// and by the length the host announces. [onProgress] runs as bytes
  /// arrive. However the download ends without
  /// [JoinHostPackageDownloaded], nothing is left at [path], and cancelling
  /// closes the connection at once. Never throws.
  Future<JoinHostPackageResult> download(
    DescriptorPackage package,
    String path, {
    void Function(int bytesReceived, int bytesTotal)? onProgress,
    CancellationToken? cancellation,
  });
}
