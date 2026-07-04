import 'package:desktop_updater/desktop_updater.dart';
import 'package:flutter/material.dart';
import 'package:launcher_domain/launcher_domain.dart';

class LauncherUpdateControllerHost extends StatefulWidget {
  const LauncherUpdateControllerHost({
    super.key,
    required this.settings,
    required this.child,
  });

  final LauncherUpdateSettings settings;
  final Widget child;

  @override
  State<LauncherUpdateControllerHost> createState() =>
      _LauncherUpdateControllerHostState();
}

class _LauncherUpdateControllerHostState
    extends State<LauncherUpdateControllerHost> {
  DesktopUpdaterController? _controller;
  _LauncherUpdateControllerKey? _key;

  @override
  void initState() {
    super.initState();
    _syncController();
  }

  @override
  void didUpdateWidget(covariant LauncherUpdateControllerHost oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncController();
  }

  @override
  void dispose() {
    _controller?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return LauncherUpdateScope(controller: _controller, child: widget.child);
  }

  void _syncController() {
    final nextKey = _LauncherUpdateControllerKey.from(widget.settings);
    if (nextKey == _key) {
      return;
    }

    _controller?.dispose();
    _controller = null;
    _key = nextKey;

    if (!widget.settings.enabled || nextKey.archiveUrl == null) {
      return;
    }

    _controller = DesktopUpdaterController(
      appArchiveUrl: nextKey.archiveUrl,
      channel: widget.settings.channel.name,
      skipInitialVersionCheck: !widget.settings.checkAutomatically,
      localization: const DesktopUpdateLocalization(
        updateAvailableText: 'Launcher update available',
        newVersionAvailableText: '{} {} is ready',
        restartText: 'Restart launcher',
        downloadText: 'Download update',
        updateFailedTooltipText: 'Launcher update check failed.',
      ),
    );
  }
}

class LauncherUpdateScope extends InheritedWidget {
  const LauncherUpdateScope({
    super.key,
    required this.controller,
    required super.child,
  });

  final DesktopUpdaterController? controller;

  static DesktopUpdaterController? maybeControllerOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<LauncherUpdateScope>()
        ?.controller;
  }

  @override
  bool updateShouldNotify(covariant LauncherUpdateScope oldWidget) {
    return controller != oldWidget.controller;
  }
}

class LauncherUpdateBanner extends StatelessWidget {
  const LauncherUpdateBanner({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = LauncherUpdateScope.maybeControllerOf(context);
    if (controller == null) {
      return const SizedBox.shrink();
    }

    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        if (!_shouldShowLauncherUpdateBanner(controller.state)) {
          return const SizedBox.shrink();
        }

        return Padding(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 0),
          child: UpdateCard(controller: controller, margin: EdgeInsets.zero),
        );
      },
    );
  }
}

class _LauncherUpdateControllerKey {
  const _LauncherUpdateControllerKey({
    required this.enabled,
    required this.checkAutomatically,
    required this.channel,
    required this.archiveUrl,
  });

  factory _LauncherUpdateControllerKey.from(LauncherUpdateSettings settings) {
    return _LauncherUpdateControllerKey(
      enabled: settings.enabled,
      checkAutomatically: settings.checkAutomatically,
      channel: settings.channel,
      archiveUrl: _parseArchiveUrl(settings.appArchiveUrl),
    );
  }

  final bool enabled;
  final bool checkAutomatically;
  final LauncherUpdateChannel channel;
  final Uri? archiveUrl;

  @override
  bool operator ==(Object other) {
    return other is _LauncherUpdateControllerKey &&
        enabled == other.enabled &&
        checkAutomatically == other.checkAutomatically &&
        channel == other.channel &&
        archiveUrl == other.archiveUrl;
  }

  @override
  int get hashCode {
    return Object.hash(enabled, checkAutomatically, channel, archiveUrl);
  }
}

bool _shouldShowLauncherUpdateBanner(UpdateState state) {
  return switch (state) {
    UpdateAvailable() ||
    UpdateFreshInstallRequired() ||
    UpdateBlockedBySupportPolicy() ||
    UpdateDownloading() ||
    UpdateReadyToInstall() => true,
    _ => false,
  };
}

Uri? _parseArchiveUrl(String value) {
  final uri = Uri.tryParse(value.trim());
  if (uri == null || !uri.hasScheme || uri.host.isEmpty) {
    return null;
  }
  return uri.scheme == 'https' || uri.scheme == 'http' ? uri : null;
}
