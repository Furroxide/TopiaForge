import 'multiplayer.dart';
import 'network_text.dart';

/// The server's game, world and size, as its host describes them.
String joinServerSummary(ServerDescriptorV1 descriptor) => [
  if (descriptor.gamemode case final gamemode?) withoutAddresses(gamemode),
  if (descriptor.world case final world?) withoutAddresses(world),
  'up to ${descriptor.maxPlayers} players',
  'Robotopia build ${descriptor.gameBuild}',
].join(' · ');

String joinDownloadSummary(JoinPlan plan) {
  final downloads = [
    for (final item in plan.packages)
      if (item.disposition == JoinPackageDisposition.download) item,
  ];
  final hosted = plan.hostServedPackages.length;
  final fromHost = switch (hosted) {
    0 => '',
    1 => ' 1 mod comes from the host itself.',
    _ => ' $hosted mods come from the host itself.',
  };
  if (downloads.isEmpty) {
    return plan.packages.every(
          (item) => item.disposition == JoinPackageDisposition.useInstalled,
        )
        ? 'Nothing to download: every mod is installed.'
        : 'Nothing to download from your mod sources.$fromHost';
  }
  final sizes = downloads.map((item) => item.resolution.sizeBytes);
  final known = sizes.every((size) => size != null);
  final total = sizes.fold<int>(0, (sum, size) => sum + (size ?? 0));
  final count = downloads.length == 1 ? '1 mod' : '${downloads.length} mods';
  return known
      ? 'Downloads $count, ${joinFormatBytes(total)}, from your mod sources.'
            '$fromHost'
      : 'Downloads $count from your mod sources.$fromHost';
}

/// [bytes] as people read a size.
String joinFormatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  const units = ['KB', 'MB', 'GB'];
  var value = bytes / 1024;
  var unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return '${value.toStringAsFixed(value >= 100 ? 0 : 1)} ${units[unit]}';
}

String joinPackageSource(JoinPlanPackage item) => switch (item.disposition) {
  JoinPackageDisposition.useInstalled => 'Installed',
  JoinPackageDisposition.download =>
    'Download from ${item.resolution.sourceName.isEmpty ? 'your source' : withoutAddresses(item.resolution.sourceName)}',
  JoinPackageDisposition.downloadFromHost => 'Served by the host',
  JoinPackageDisposition.skip => 'Not needed here',
  JoinPackageDisposition.blocked => 'Not available',
};

String joinPackageSize(JoinPlanPackage item) {
  if (item.disposition == JoinPackageDisposition.useInstalled) {
    return 'Installed';
  }
  final size = item.resolution.sizeBytes;
  return size == null ? 'Size not listed' : joinFormatBytes(size);
}

abstract final class JoinConsentText {
  static const password =
      'This server asks for a password. Robotopia asks you for it '
      'when you join; the launcher never stores it.';
  static const unknownCapabilities =
      'What the mods served by the host may do is not known until '
      'each one is downloaded and checked.';
  static const warning =
      'Mods run as code inside Robotopia, with the same access to '
      'this computer as the game. Capabilities describe what a mod '
      'says it does; they do not limit it. Join only servers whose '
      'mods you trust.';
  static const ownedGame =
      'Robotopia is running from this launcher. If it runs other '
      'mods than the server, joining asks it to quit, as its QUIT '
      'button would, and starts it again for the server.';
  static const foreignGame =
      'Robotopia is running, and this launcher did not start it. '
      'Close the game before you join; the launcher never closes '
      'it for you.';
}
