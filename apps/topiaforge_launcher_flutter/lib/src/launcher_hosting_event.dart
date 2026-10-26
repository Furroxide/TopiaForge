part of 'launcher_event.dart';

sealed class HostingEvent extends LauncherEvent {
  const HostingEvent();
}

final class HostingOpened extends HostingEvent {
  const HostingOpened();
}

final class HostingPreferencesLoaded extends HostingEvent {
  const HostingPreferencesLoaded(this.generation, this.preferences);
  final int generation;
  final MultiplayerPreferences? preferences;
}

final class HostingReviewRequested extends HostingEvent {
  const HostingReviewRequested(this.choices);
  final HostingChoices choices;
}

final class HostingPrepared extends HostingEvent {
  const HostingPrepared(this.generation, this.preparation);
  final int generation;
  final HostingPreparation preparation;
}

final class HostingConfirmed extends HostingEvent {
  const HostingConfirmed({required this.acknowledgeDisclosure});
  final bool acknowledgeDisclosure;
}

final class HostingStarted extends HostingEvent {
  const HostingStarted(this.generation, this.result);
  final int generation;
  final LaunchResult result;
}

final class HostingWaitExpired extends HostingEvent {
  const HostingWaitExpired(this.generation);
  final int generation;
}

final class HostingClosed extends HostingEvent {
  const HostingClosed();
}

final class HostingEditRequested extends HostingEvent {
  const HostingEditRequested();
}

sealed class ServerCleanupEvent extends LauncherEvent {
  const ServerCleanupEvent();
}

final class ServerCleanupRequested extends ServerCleanupEvent {
  const ServerCleanupRequested({this.profiles});
  final Set<String>? profiles;
}

final class ServerCleanupPlanned extends ServerCleanupEvent {
  const ServerCleanupPlanned(
    this.generation,
    this.plan, {
    required this.reviewing,
  });
  final int generation;
  final ServerContentCleanupPlan? plan;
  final bool reviewing;
}

final class ServerCleanupConfirmed extends ServerCleanupEvent {
  const ServerCleanupConfirmed(this.fingerprint);
  final String fingerprint;
}

final class ServerCleanupFinished extends ServerCleanupEvent {
  const ServerCleanupFinished(this.generation, this.result);
  final int generation;
  final ServerContentCleanupResult? result;
}

final class ServerCleanupClosed extends ServerCleanupEvent {
  const ServerCleanupClosed();
}
