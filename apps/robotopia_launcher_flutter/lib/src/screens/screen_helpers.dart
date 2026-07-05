part of '../screens.dart';

void _add(BuildContext context, LauncherEvent event) {
  context.read<LauncherBloc>().add(event);
}

const String _restartRequiredTooltip =
    'Robotopia must be relaunched before this pending mod change is applied to the running game. This clears after the loader starts with the current mod state.';

StatusPill _restartPill(
  BuildContext context,
  LauncherState state, {
  String label = 'Restart',
}) {
  return StatusPill(
    label: label,
    tone: StatusTone.warning,
    icon: Icons.restart_alt,
    tooltip: _restartRequiredTooltip,
    onPressed: state.canLaunch && !state.isBusy
        ? () => _confirmRestart(context)
        : null,
  );
}

Future<void> _confirmRestart(BuildContext context) {
  return _confirm(
    context,
    title: 'Restart Robotopia?',
    message:
        'The launcher will close the running Robotopia process for this install if one is active, then launch it again with the selected profile. Unsaved in-game progress may be lost.',
    confirmLabel: 'Restart',
    action: () => _add(context, const GameRestartRequested()),
  );
}

StatusPill _componentPill(String label, ComponentState state) {
  return StatusPill(
    label: '$label ${state.name}',
    tone: switch (state) {
      ComponentState.ready => StatusTone.good,
      ComponentState.partial => StatusTone.warning,
      ComponentState.missing => StatusTone.danger,
    },
    icon: switch (state) {
      ComponentState.ready => Icons.check_circle,
      ComponentState.partial => Icons.build_circle,
      ComponentState.missing => Icons.error,
    },
  );
}

// Game-compatibility pill. WARN-ONLY: a broken critical binding shows amber ("may not work"), never a red
// "can't launch" signal — compatibility never blocks the game.
StatusPill _compatPill(GameCompatStatus compat) {
  final (
    StatusTone tone,
    String label,
    IconData icon,
  ) = switch (compat.status) {
    'ok' => (StatusTone.good, 'Mods compatible', Icons.verified),
    'broken' => (
      StatusTone.warning,
      '${compat.errorCount} mod feature(s) may not work',
      Icons.warning_amber,
    ),
    'skipped' => (
      StatusTone.neutral,
      'Compat: no game detected',
      Icons.help_outline,
    ),
    _ => (StatusTone.neutral, 'Compat: not checked', Icons.help_outline),
  };
  return StatusPill(
    label: label,
    tone: tone,
    icon: icon,
    tooltip: compat.gameVersionLabel.isNotEmpty
        ? 'Checked against game version: ${compat.gameVersionLabel}'
        : 'Mod reflection bindings checked against the installed game.',
  );
}

Widget _compatFindingTile(CompatFinding finding) {
  final color = switch (finding.severity) {
    IssueSeverity.info => QuantumWorksPalette.accentDark,
    IssueSeverity.warning => QuantumWorksPalette.warning,
    IssueSeverity.error => QuantumWorksPalette.danger,
  };
  return Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(_issueIcon(finding.severity), size: 16, color: color),
        const SizedBox(width: 6),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '${finding.modId}: ${finding.feature}',
                style: TextStyle(
                  color: color,
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                ),
              ),
              if (finding.detail.isNotEmpty)
                Text(finding.detail, style: const TextStyle(fontSize: 11)),
            ],
          ),
        ),
      ],
    ),
  );
}

Widget _keyValue(String label, String value) {
  return Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: 130,
          child: Text(
            label,
            style: const TextStyle(
              color: QuantumWorksPalette.mutedText,
              fontSize: 12,
            ),
          ),
        ),
        Expanded(
          child: SelectableText(value, style: const TextStyle(fontSize: 12)),
        ),
      ],
    ),
  );
}

Widget _issueTile(LauncherIssue issue) {
  final color = switch (issue.severity) {
    IssueSeverity.info => QuantumWorksPalette.accentDark,
    IssueSeverity.warning => QuantumWorksPalette.warning,
    IssueSeverity.error => QuantumWorksPalette.danger,
  };
  return Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(_issueIcon(issue.severity), size: 16, color: color),
        const SizedBox(width: 6),
        Expanded(
          child: Text(
            issue.message,
            style: TextStyle(color: color, fontSize: 12),
          ),
        ),
      ],
    ),
  );
}

IconData _issueIcon(IssueSeverity severity) {
  return switch (severity) {
    IssueSeverity.info => Icons.info,
    IssueSeverity.warning => Icons.warning,
    IssueSeverity.error => Icons.error,
  };
}

List<LauncherIssue> _issuesForMod(LauncherState state, String modId) {
  return state.resolution.issues
      .where((issue) => issue.subjectId?.toLowerCase() == modId.toLowerCase())
      .toList();
}

RegistryMod? _registryModForInstalled(LauncherState state, String modId) {
  for (final mod in state.registryMods) {
    if (mod.manifest.id.toLowerCase() == modId.toLowerCase()) {
      return mod;
    }
  }
  return null;
}

RegistryMod? _availableUpdateForMod(LauncherState state, InstalledMod mod) {
  final registryMod = _registryModForInstalled(state, mod.id);
  return registryMod != null && registryMod.updateAvailable
      ? registryMod
      : null;
}

List<RegistryMod> _registryModsForDiscovery(
  LauncherState state, {
  Iterable<RegistryMod>? mods,
}) {
  final source = mods ?? state.registryMods;
  if (state.developerMode) {
    return source is List<RegistryMod> ? source : source.toList();
  }

  final regular = <RegistryMod>[];
  final framework = <RegistryMod>[];
  for (final mod in source) {
    if (_isFrameworkRegistryMod(mod)) {
      framework.add(mod);
    } else {
      regular.add(mod);
    }
  }
  return [...regular, ...framework];
}

bool _isFrameworkRegistryMod(RegistryMod mod) {
  return mod.manifest.category.trim().toLowerCase() == 'framework';
}

String _updateTooltip(RegistryMod mod) {
  return 'Update available: installed ${mod.installedVersion}, registry ${mod.manifest.version}.';
}

bool _canPreviewRegistryPackage(LauncherState state, RegistryMod mod) {
  return !state.isBusy &&
      state.gameInstall != null &&
      (mod.downloadUrl.startsWith('file:') ||
          mod.downloadUrl.startsWith('https://'));
}

void _previewRegistryPackage(
  BuildContext context,
  RegistryMod mod, {
  bool switchToMods = false,
}) {
  final reference = Uri.parse(mod.downloadUrl).scheme == 'file'
      ? Uri.parse(mod.downloadUrl).toFilePath(windows: Platform.isWindows)
      : mod.downloadUrl;
  _add(
    context,
    PackagePreviewRequested(
      reference,
      expectedSha256: mod.packageSha256,
      sourceId: mod.sourceId,
      sourceName: mod.sourceName,
    ),
  );
  if (switchToMods) {
    _add(context, const LauncherSectionSelected(LauncherSection.mods));
  }
}

Future<void> _chooseGameFolder(BuildContext context) async {
  final path = await getDirectoryPath(
    confirmButtonText: 'Select Robotopia Folder',
  );
  if (path != null && context.mounted) {
    _add(context, GameDirectorySelected(path));
  }
}

Future<void> _choosePackage(BuildContext context) async {
  const typeGroup = XTypeGroup(
    label: 'Robotopia packages',
    extensions: ['robotopiamod'],
  );
  final file = await openFile(acceptedTypeGroups: [typeGroup]);
  if (file != null && context.mounted) {
    _add(context, PackagePreviewRequested(file.path));
  }
}

Future<void> _checkLauncherUpdates(
  BuildContext context,
  DesktopUpdaterController controller,
  LauncherUpdateChannel channel,
) async {
  final result = await controller.checkForUpdates();
  if (!context.mounted) {
    return;
  }

  final (title, message) = switch (result) {
    ManualUpdateCheckUpToDate() => (
      'Launcher is up to date',
      'No newer launcher build is available on the ${_updateChannelLabel(channel)} channel.',
    ),
    ManualUpdateCheckAvailable(:final descriptor) => (
      'Launcher update available',
      '${descriptor.appName} ${descriptor.version} is ready to download.',
    ),
    ManualUpdateCheckFreshInstallRequired(:final descriptor) => (
      'Fresh install required',
      '${descriptor.appName} ${descriptor.version} is available, but this update must be installed from a fresh download.',
    ),
    ManualUpdateCheckBlockedBySupportPolicy(:final descriptor) => (
      'Launcher update required',
      '${descriptor.appName} ${descriptor.version} is required before this launcher version can continue receiving support.',
    ),
    ManualUpdateCheckFailed(:final error) => (
      'Could not check for updates',
      error.toString(),
    ),
  };

  await showDialog<void>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('OK'),
        ),
      ],
    ),
  );
}

String _updateChannelLabel(LauncherUpdateChannel channel) {
  return switch (channel) {
    LauncherUpdateChannel.release => 'Release',
    LauncherUpdateChannel.beta => 'Beta',
    LauncherUpdateChannel.nightly => 'Nightly',
  };
}

Future<void> _exportProfile(BuildContext context) async {
  final profile = context.read<LauncherBloc>().state.selectedProfile;
  if (profile == null) {
    return;
  }
  final location = await getSaveLocation(
    suggestedName:
        '${profile.name.replaceAll(RegExp(r'[^A-Za-z0-9_.-]+'), '_')}.robotopiaprofile.json',
  );
  if (location != null && context.mounted) {
    _add(context, SelectedProfileExported(location.path));
  }
}

Future<void> _importProfile(BuildContext context) async {
  const typeGroup = XTypeGroup(
    label: 'Robotopia profile',
    extensions: ['json'],
  );
  final file = await openFile(acceptedTypeGroups: [typeGroup]);
  if (file != null && context.mounted) {
    _add(context, ProfileImported(file.path));
  }
}

Future<void> _confirm(
  BuildContext context, {
  required String title,
  required String message,
  required VoidCallback action,
  String confirmLabel = 'Continue',
}) async {
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(false),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(true),
          child: Text(confirmLabel),
        ),
      ],
    ),
  );

  if (confirmed == true && context.mounted) {
    action();
  }
}
