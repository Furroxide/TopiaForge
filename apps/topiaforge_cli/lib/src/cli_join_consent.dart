import 'package:launcher_domain/launcher_domain.dart';
import 'cli_multiplayer_text.dart';

/// The launcher's consent sheet as lines, using its shared words and facts.
Iterable<String> cliJoinConsent(
  JoinSessionState state, {
  required bool voice,
  required Set<String> profileIds,
  required JoinGameRunning running,
}) sync* {
  final plan = state.plan!;
  final server = plan.server.descriptor;
  yield 'Join ${withoutAddresses(server.name)}?';
  yield joinServerSummary(server);
  if (server.passwordRequired) yield JoinConsentText.password;
  yield state.privacy == MultiplayerAddressPrivacy.direct
      ? 'Direct connection: the host will see your IP address.'
      : "Privacy: Hide my IP is on, so the host won't see your address.";
  yield voice && server.voice
      ? CliMultiplayerText.voiceOn
      : CliMultiplayerText.voiceOff;
  yield 'Mods (${plan.packages.length})';
  yield 'Mod | Source | SHA-256 | Size';
  for (final item in plan.packages) {
    final package = item.package;
    final name = item.manifest?.name.trim() ?? '';
    yield '${withoutAddresses(name.isEmpty ? package.id : name)} '
        '(${withoutAddresses(package.id)}@${withoutAddresses(package.version)}) | '
        '${joinPackageSource(item)} | ${package.sha256} | ${joinPackageSize(item)}';
  }
  yield joinDownloadSummary(plan);
  yield 'What these mods may do';
  if (!plan.hasUnverifiedPackages || plan.capabilities.isNotEmpty) {
    yield plan.capabilities.isEmpty
        ? 'No capabilities declared.'
        : withoutAddresses(plan.capabilities.join(', '));
  }
  if (plan.hasUnverifiedPackages) yield JoinConsentText.unknownCapabilities;
  yield JoinConsentText.warning;
  yield "${profileIds.contains(plan.derivedProfile.id) ? 'Updates' : 'Creates'} "
      "profile '${withoutAddresses(plan.derivedProfile.name)}'";
  if (running == JoinGameRunning.owned) yield JoinConsentText.ownedGame;
  if (running == JoinGameRunning.foreign) yield JoinConsentText.foreignGame;
  if (plan.hasUnverifiedPackages) {
    yield JoinHostServedText.title;
    yield JoinHostServedText.warning;
    yield JoinHostServedText.checkbox;
    yield JoinHostServedText.untickedHint;
  }
}
