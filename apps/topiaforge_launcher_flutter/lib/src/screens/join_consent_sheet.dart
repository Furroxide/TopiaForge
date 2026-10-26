part of '../screens.dart';

/// The consent sheet: what joining the server installs, what those mods may
/// do, how the session treats the player's address and microphone, the
/// profile it saves, and what happens to a running game. Mods only the host
/// has add the red developer-mode section, whose box Join waits for.
/// Nothing is downloaded, installed or started before the player presses
/// Join, and the focus starts on Cancel, so a stray Enter never joins.
class _JoinConsentSheet extends StatelessWidget {
  const _JoinConsentSheet({required this.flow, required this.state});

  final JoinFlow flow;
  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final join = flow.join!;
    final plan = join.plan!;
    final descriptor = plan.server.descriptor;
    final name = withoutAddresses(descriptor.name);
    final profileName = withoutAddresses(plan.derivedProfile.name);
    final updates = state.profiles.any(
      (profile) => profile.id == plan.derivedProfile.id,
    );
    final hostServed = plan.hasUnverifiedPackages;
    final closesGame = flow.gameRunning == JoinGameRunning.owned;
    final theme = Theme.of(context);
    return _JoinFrame(
      key: const Key('join-consent-sheet'),
      title: 'Join $name?',
      icon: Icons.groups,
      tone: TopiaForgePalette.launchDark,
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(joinServerSummary(descriptor), style: theme.textTheme.bodySmall),
          if (descriptor.passwordRequired) ...[
            const SizedBox(height: 6),
            const _SheetLine(
              icon: Icons.password,
              text: JoinConsentText.password,
            ),
          ],
          const SizedBox(height: 12),
          _SheetLine(
            key: const Key('join-privacy-line'),
            icon: join.privacy == MultiplayerAddressPrivacy.direct
                ? Icons.public
                : Icons.shield_outlined,
            text: join.privacy == MultiplayerAddressPrivacy.direct
                ? MultiplayerPrivacyText.sheetPrivacyDirect
                : MultiplayerPrivacyText.sheetPrivacyHidden,
          ),
          const SizedBox(height: 4),
          _SheetLine(
            key: const Key('join-voice-line'),
            icon: flow.voice && descriptor.voice
                ? Icons.mic_none
                : Icons.mic_off_outlined,
            text: flow.voice && descriptor.voice
                ? MultiplayerPrivacyText.sheetVoiceOn
                : MultiplayerPrivacyText.sheetVoiceOff,
          ),
          const SizedBox(height: 16),
          Text(
            'Mods (${plan.packages.length})',
            style: theme.textTheme.titleSmall,
          ),
          const SizedBox(height: 6),
          _JoinPackageTable(plan: plan),
          const SizedBox(height: 6),
          Text(joinDownloadSummary(plan), style: theme.textTheme.bodySmall),
          const SizedBox(height: 16),
          Text('What these mods may do', style: theme.textTheme.titleSmall),
          const SizedBox(height: 6),
          // No manifest anyone vouches for comes with a mod only the host
          // has, so what it may do is not known before it is checked.
          if (!hostServed || plan.capabilities.isNotEmpty)
            CapabilityChips(capabilities: plan.capabilities),
          if (hostServed) ...[
            if (plan.capabilities.isNotEmpty) const SizedBox(height: 6),
            Text(
              JoinConsentText.unknownCapabilities,
              key: const Key('join-host-served-capabilities'),
              style: theme.textTheme.bodySmall,
            ),
          ],
          const SizedBox(height: 8),
          const SessionNotice(
            key: Key('join-code-warning'),
            tone: StatusTone.warning,
            icon: Icons.warning_amber,
            message: JoinConsentText.warning,
          ),
          const SizedBox(height: 12),
          _SheetLine(
            key: const Key('join-profile-line'),
            icon: Icons.layers_outlined,
            text: updates
                ? "Updates profile '$profileName'"
                : "Creates profile '$profileName'",
          ),
          if (closesGame) ...[
            const SizedBox(height: 4),
            const _SheetLine(
              key: Key('join-closes-game'),
              icon: Icons.restart_alt,
              text: JoinConsentText.ownedGame,
            ),
          ] else if (flow.gameRunning == JoinGameRunning.foreign) ...[
            const SizedBox(height: 8),
            const SessionNotice(
              key: Key('join-foreign-game'),
              tone: StatusTone.warning,
              message: JoinConsentText.foreignGame,
            ),
          ],
          if (hostServed) ...[
            const SizedBox(height: 12),
            _HostServedSection(flow: flow),
          ],
        ],
      ),
      actions: [
        TextButton(
          key: const Key('join-consent-cancel'),
          autofocus: true,
          onPressed: () => _add(context, MultiplayerJoinCancelled(flow.id)),
          child: const Text('Cancel'),
        ),
        FilledButton.icon(
          key: const Key('join-consent-join'),
          onPressed: hostServed && !flow.hostServedTicked
              ? null
              : () => _add(
                  context,
                  MultiplayerJoinConsented(
                    flow: flow.id,
                    consent: join,
                    closeOwnedGame: closesGame,
                  ),
                ),
          icon: const Icon(Icons.login),
          label: const Text('Join'),
        ),
      ],
    );
  }
}

String _formatBytes(int bytes) => joinFormatBytes(bytes);

/// One line of the sheet with its icon.
class _SheetLine extends StatelessWidget {
  const _SheetLine({super.key, required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Padding(
        padding: const EdgeInsets.only(top: 1, right: 8),
        child: Icon(icon, size: 18, color: TopiaForgePalette.accentDark),
      ),
      Expanded(child: Text(text)),
    ],
  );
}

/// The plan's packages: name, id@version, source, SHA-256 and size. Wide
/// windows show columns; narrow ones, and large text, stack each package's
/// facts under its name.
class _JoinPackageTable extends StatelessWidget {
  const _JoinPackageTable({required this.plan});

  final JoinPlan plan;

  @override
  Widget build(BuildContext context) {
    final scale = MediaQuery.textScalerOf(context).scale(1);
    return LayoutBuilder(
      builder: (context, constraints) {
        final wide = constraints.maxWidth >= 620 && scale <= 1.3;
        return DecoratedBox(
          decoration: BoxDecoration(
            border: Border.all(color: TopiaForgePalette.surfaceTint, width: 2),
            borderRadius: BorderRadius.circular(14),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (wide) const _PackageHeader(),
              for (var index = 0; index < plan.packages.length; index++)
                _PackageRow(
                  item: plan.packages[index],
                  wide: wide,
                  shaded: index.isOdd,
                ),
            ],
          ),
        );
      },
    );
  }
}

class _PackageHeader extends StatelessWidget {
  const _PackageHeader();

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.labelLarge;
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 6),
      child: Row(
        children: [
          Expanded(flex: 4, child: Text('Mod', style: style)),
          Expanded(flex: 3, child: Text('Source', style: style)),
          Expanded(flex: 3, child: Text('SHA-256', style: style)),
          Expanded(flex: 2, child: Text('Size', style: style)),
        ],
      ),
    );
  }
}

class _PackageRow extends StatelessWidget {
  const _PackageRow({
    required this.item,
    required this.wide,
    required this.shaded,
  });

  final JoinPlanPackage item;
  final bool wide;
  final bool shaded;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final package = item.package;
    final manifestName = item.manifest?.name.trim() ?? '';
    // A mod only the host has is named by the host alone, so its id and
    // version are held to the same rule as every other word from the host.
    final name = withoutAddresses(
      manifestName.isEmpty ? package.id : manifestName,
    );
    final identity =
        '${withoutAddresses(package.id)}@${withoutAddresses(package.version)}';
    final source = joinPackageSource(item);
    final sha = Tooltip(
      message: package.sha256,
      child: Text(
        '${package.sha256.substring(0, 12)}…',
        semanticsLabel: 'SHA-256 ${package.sha256}',
        style: theme.textTheme.bodySmall?.copyWith(
          fontFamily: 'Consolas',
          color: TopiaForgePalette.text,
        ),
      ),
    );
    final size = joinPackageSize(item);
    final nameBlock = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(name, style: theme.textTheme.titleSmall),
        Text(identity, style: theme.textTheme.bodySmall),
      ],
    );
    return Container(
      color: shaded ? TopiaForgePalette.surfaceAlt : null,
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: wide
          ? Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(flex: 4, child: nameBlock),
                Expanded(flex: 3, child: Text(source)),
                Expanded(flex: 3, child: sha),
                Expanded(flex: 2, child: Text(size)),
              ],
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                nameBlock,
                const SizedBox(height: 4),
                Wrap(
                  spacing: 12,
                  runSpacing: 2,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [Text(source), sha, Text(size)],
                ),
              ],
            ),
    );
  }
}
