part of 'multiplayer_repository.dart';

mixin _HostingRepository implements MultiplayerHostingGateway {
  MultiplayerContentHost? get _content;
  String? get _serviceOrigin;
  Future<MultiplayerServiceCheck> readServiceStatus({
    CancellationToken? cancellation,
  });

  HostingService? get _hosting {
    final content = _content;
    return content is LauncherRepository
        ? HostingService(
            launcher: content as LauncherRepository,
            multiplayer: this,
            serviceStatus: (token) => readServiceStatus(cancellation: token),
            serviceOrigin: _serviceOrigin,
          )
        : null;
  }

  @override
  Future<HostingPreparation> prepareHosting(
    GameInstall install,
    LauncherProfile profile,
    HostingChoices choices, {
    required bool developerMode,
    required CancellationToken cancellation,
  }) async =>
      await _hosting?.prepare(
        install,
        profile,
        choices,
        developerMode: developerMode,
        cancellation: cancellation,
      ) ??
      const HostingRefused('Hosting is unavailable in this launcher.');

  @override
  Future<LaunchResult> startHosting(
    GameInstall install,
    HostingPlan plan, {
    required String intentId,
    required bool acknowledgeDisclosure,
    required bool developerMode,
    required CancellationToken cancellation,
  }) async =>
      await _hosting?.start(
        install,
        plan,
        intentId: intentId,
        acknowledgeDisclosure: acknowledgeDisclosure,
        developerMode: developerMode,
        cancellation: cancellation,
      ) ??
      const LaunchResult(
        started: false,
        message: 'Hosting is unavailable in this launcher.',
      );
}
