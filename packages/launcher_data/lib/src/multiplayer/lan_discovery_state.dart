/// What LAN discovery has found, as the launcher shows it. The types are the
/// domain's, so the launcher's screens can hold them; this library keeps the
/// data layer's imports and its barrel export as they were.
library;

export 'package:launcher_domain/launcher_domain.dart'
    show
        LanBrowseSnapshot,
        LanDiscoveryCounters,
        LanDiscoveryProblem,
        LanDiscoveryStatus;
