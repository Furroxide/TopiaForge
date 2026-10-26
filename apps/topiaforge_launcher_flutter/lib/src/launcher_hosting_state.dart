part of 'launcher_multiplayer_state.dart';

enum HostingPhase {
  closed,
  loading,
  editing,
  preparing,
  review,
  starting,
  waiting,
  hosting,
  failed,
}

final class HostingView {
  const HostingView({
    this.generation = 0,
    this.phase = HostingPhase.closed,
    this.preferences,
    this.choices,
    this.plan,
    this.message = '',
    this.canAllowDirect = false,
    this.inviteCode,
  });
  final int generation;
  final HostingPhase phase;
  final MultiplayerPreferences? preferences;
  final HostingChoices? choices;
  final HostingPlan? plan;
  final String message;
  final bool canAllowDirect;
  final String? inviteCode;
  bool get busy => const {
    HostingPhase.loading,
    HostingPhase.preparing,
    HostingPhase.starting,
  }.contains(phase);
}

final class ServerCleanupView {
  const ServerCleanupView({
    this.generation = 0,
    this.busy = false,
    this.catalog,
    this.plan,
    this.reviewing = false,
    this.message = '',
  });
  final int generation;
  final bool busy;
  final ServerContentCleanupPlan? catalog;
  final ServerContentCleanupPlan? plan;
  final bool reviewing;
  final String message;
}
