import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_ui/launcher_ui.dart';

import 'launcher_bloc.dart';
import 'launcher_event.dart';
import 'launcher_section.dart';
import 'launcher_state.dart';
import 'launcher_update_controller.dart';
import 'screens.dart';

class RobotopiaLauncherApp extends StatelessWidget {
  const RobotopiaLauncherApp({
    super.key,
    required this.repository,
    this.developerRepository,
  });

  final LauncherRepository repository;
  final DeveloperRepository? developerRepository;

  @override
  Widget build(BuildContext context) {
    return RepositoryProvider.value(
      value: repository,
      child: BlocProvider(
        create: (_) =>
            LauncherBloc(repository, developerRepository: developerRepository)
              ..add(const LauncherStarted()),
        child: MaterialApp(
          title: 'QuantumWorks',
          debugShowCheckedModeBanner: false,
          theme: buildQuantumWorksTheme(),
          home: const LauncherShell(),
        ),
      ),
    );
  }
}

class LauncherShell extends StatelessWidget {
  const LauncherShell({super.key});

  @override
  Widget build(BuildContext context) {
    return BlocBuilder<LauncherBloc, LauncherState>(
      builder: (context, state) {
        final visibleSections = state.visibleSections;
        final selectedIndex = visibleSections.indexOf(state.section);
        return LauncherUpdateControllerHost(
          settings: state.launcherUpdates,
          child: Scaffold(
            body: QuantumWorksBackdrop(
              child: Row(
                children: [
                  NavigationRail(
                    minWidth: 86,
                    labelType: NavigationRailLabelType.all,
                    selectedIndex: selectedIndex < 0 ? null : selectedIndex,
                    onDestinationSelected: (index) => context
                        .read<LauncherBloc>()
                        .add(LauncherSectionSelected(visibleSections[index])),
                    destinations: [
                      for (final section in visibleSections)
                        _destinationFor(section, state.availableModUpdateCount),
                    ],
                  ),
                  const VerticalDivider(width: 1),
                  Expanded(
                    child: Column(
                      children: [
                        _TopBar(state: state),
                        if (state.isBusy)
                          const LinearProgressIndicator(minHeight: 3),
                        const LauncherUpdateBanner(),
                        Expanded(child: LauncherBody(state: state)),
                        _StatusBar(state: state),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}

NavigationRailDestination _destinationFor(
  LauncherSection section,
  int modUpdateCount,
) {
  return switch (section) {
    LauncherSection.home => const NavigationRailDestination(
      icon: Icon(Icons.rocket_launch_outlined),
      selectedIcon: Icon(Icons.rocket_launch),
      label: Text('Home'),
    ),
    LauncherSection.setup => const NavigationRailDestination(
      icon: Icon(Icons.tune_outlined),
      selectedIcon: Icon(Icons.tune),
      label: Text('Setup'),
    ),
    LauncherSection.mods => NavigationRailDestination(
      icon: _badgedModUpdateIcon(Icons.extension_outlined, modUpdateCount),
      selectedIcon: _badgedModUpdateIcon(Icons.extension, modUpdateCount),
      label: const Text('Mods'),
    ),
    LauncherSection.browse => NavigationRailDestination(
      icon: _badgedModUpdateIcon(Icons.travel_explore_outlined, modUpdateCount),
      selectedIcon: _badgedModUpdateIcon(Icons.travel_explore, modUpdateCount),
      label: const Text('Browse'),
    ),
    LauncherSection.profiles => const NavigationRailDestination(
      icon: Icon(Icons.layers_outlined),
      selectedIcon: Icon(Icons.layers),
      label: Text('Profiles'),
    ),
    LauncherSection.developer => const NavigationRailDestination(
      icon: Icon(Icons.code_outlined),
      selectedIcon: Icon(Icons.code),
      label: Text('Dev'),
    ),
    LauncherSection.diagnostics => const NavigationRailDestination(
      icon: Icon(Icons.monitor_heart_outlined),
      selectedIcon: Icon(Icons.monitor_heart),
      label: Text('Diag'),
    ),
    LauncherSection.settings => const NavigationRailDestination(
      icon: Icon(Icons.settings_outlined),
      selectedIcon: Icon(Icons.settings),
      label: Text('Settings'),
    ),
  };
}

Widget _badgedModUpdateIcon(IconData icon, int modUpdateCount) {
  if (modUpdateCount <= 0) {
    return Icon(icon);
  }
  return Badge.count(
    count: modUpdateCount,
    maxCount: 99,
    backgroundColor: QuantumWorksPalette.warning,
    textColor: QuantumWorksPalette.white,
    child: Icon(icon),
  );
}

class _TopBar extends StatelessWidget {
  const _TopBar({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<LauncherBloc>();
    final selectedProfile = state.selectedProfile;
    return Container(
      height: 72,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: const BoxDecoration(
        color: Color(0xEEFFF7E9),
        border: Border(
          bottom: BorderSide(color: QuantumWorksPalette.launchDark, width: 4),
        ),
        boxShadow: [
          BoxShadow(
            color: Color(0x22000000),
            offset: Offset(0, 6),
            blurRadius: 16,
          ),
        ],
      ),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final compact = constraints.maxWidth < 760;
          return Row(
            children: [
              QuantumWorksLogo(height: compact ? 30 : 40),
              SizedBox(width: compact ? 8 : 22),
              Expanded(
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: ConstrainedBox(
                    constraints: BoxConstraints(maxWidth: compact ? 180 : 220),
                    child: Container(
                      height: 40,
                      padding: const EdgeInsets.symmetric(horizontal: 10),
                      decoration: BoxDecoration(
                        color: QuantumWorksPalette.surface,
                        border: Border.all(
                          color: QuantumWorksPalette.borderStrong,
                          width: 2,
                        ),
                        borderRadius: BorderRadius.circular(20),
                        boxShadow: const [
                          BoxShadow(
                            color: Color(0x1FCC620E),
                            offset: Offset(-3, 4),
                            blurRadius: 0,
                          ),
                        ],
                      ),
                      child: DropdownButtonHideUnderline(
                        child: DropdownButton<String>(
                          isExpanded: true,
                          value: selectedProfile?.id,
                          icon: const Icon(Icons.expand_more),
                          items: state.profiles
                              .map(
                                (profile) => DropdownMenuItem(
                                  value: profile.id,
                                  child: Text(
                                    profile.name,
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                ),
                              )
                              .toList(),
                          onChanged: (value) {
                            if (value != null) {
                              bloc.add(ProfileSelected(value));
                            }
                          },
                          hint: const Text(
                            'Profile',
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
              Tooltip(
                message: 'Refresh launcher state',
                child: IconButton(
                  onPressed: state.isBusy
                      ? null
                      : () => bloc.add(const LauncherRefreshRequested()),
                  icon: const Icon(Icons.refresh),
                ),
              ),
              const SizedBox(width: 8),
              FilledButton.icon(
                onPressed: state.canStartLaunchFlow && !state.isBusy
                    ? () => bloc.add(const GameLaunchRequested())
                    : null,
                style: FilledButton.styleFrom(
                  backgroundColor: QuantumWorksPalette.discord,
                  disabledBackgroundColor: QuantumWorksPalette.surfaceTint,
                  foregroundColor: QuantumWorksPalette.white,
                  disabledForegroundColor: QuantumWorksPalette.faintText,
                ),
                icon: const Icon(Icons.play_arrow),
                label: Text(
                  selectedProfile?.launchSettings.safeMode == true
                      ? 'Launch Safe'
                      : 'Launch',
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}

class _StatusBar extends StatelessWidget {
  const _StatusBar({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final install = state.gameInstall;
    final bloc = context.read<LauncherBloc>();
    final noInstall = install == null;
    final setupBlocked = install != null && !install.canLaunch;
    final needsRepair = install?.needsRepair == true && !setupBlocked;
    final label = noInstall
        ? 'No game selected'
        : setupBlocked
        ? 'Setup needed'
        : needsRepair
        ? 'Repair needed'
        : 'Runtime ready';
    final tone = noInstall
        ? StatusTone.neutral
        : setupBlocked
        ? StatusTone.danger
        : needsRepair
        ? StatusTone.warning
        : StatusTone.good;
    final icon = noInstall
        ? Icons.folder_off
        : setupBlocked
        ? Icons.error
        : needsRepair
        ? Icons.build
        : Icons.check_circle;
    final VoidCallback? action = state.isBusy
        ? null
        : noInstall || setupBlocked
        ? () => bloc.add(const LauncherSectionSelected(LauncherSection.setup))
        : needsRepair
        ? () => bloc.add(const RuntimeRepaired())
        : null;
    final tooltip = noInstall
        ? 'Open Setup to select a Robotopia install.'
        : setupBlocked
        ? 'Open Setup to resolve the game install issue.'
        : needsRepair
        ? 'Repair the runtime now.'
        : null;
    return Container(
      height: 34,
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: const BoxDecoration(
        color: Color(0xEEFFF7E9),
        border: Border(
          top: BorderSide(color: QuantumWorksPalette.borderStrong, width: 2),
        ),
      ),
      child: Row(
        children: [
          StatusPill(
            label: label,
            tone: tone,
            icon: icon,
            tooltip: tooltip,
            onPressed: action,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              state.statusMessage,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          if (state.errorMessage != null)
            Flexible(
              child: Text(
                state.errorMessage!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  color: QuantumWorksPalette.danger,
                  fontSize: 12,
                ),
              ),
            ),
        ],
      ),
    );
  }
}
