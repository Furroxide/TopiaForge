import 'dart:io';

import 'package:desktop_updater/desktop_updater.dart';
import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_ui/launcher_ui.dart';

import 'launcher_bloc.dart';
import 'launcher_event.dart';
import 'launcher_section.dart';
import 'launcher_state.dart';
import 'launcher_update_controller.dart';

part 'screens/browse_profiles_screen.dart';
part 'screens/developer_screen.dart';
part 'screens/developer_environment_pane.dart';
part 'screens/developer_packages_pane.dart';
part 'screens/developer_ugc_pane.dart';
part 'screens/diagnostics_settings_screen.dart';
part 'screens/home_discovery.dart';
part 'screens/home_first_run.dart';
part 'screens/home_launch_pane.dart';
part 'screens/home_profiles.dart';
part 'screens/home_screen.dart';
part 'screens/mods_screen.dart';
part 'screens/setup_screen.dart';
part 'screens/settings_screen.dart';
part 'screens/screen_dialog_helpers.dart';
part 'screens/screen_helpers.dart';

class LauncherBody extends StatelessWidget {
  const LauncherBody({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return switch (state.section) {
      LauncherSection.home => HomeScreen(state: state),
      LauncherSection.setup => SetupScreen(state: state),
      LauncherSection.mods => ModsScreen(state: state),
      LauncherSection.browse => BrowseScreen(state: state),
      LauncherSection.profiles => ProfilesScreen(state: state),
      LauncherSection.developer => DeveloperScreen(state: state),
      LauncherSection.diagnostics => DiagnosticsScreen(state: state),
      LauncherSection.settings => SettingsScreen(state: state),
    };
  }
}
