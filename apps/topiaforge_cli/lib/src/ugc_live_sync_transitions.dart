import 'package:launcher_domain/launcher_domain.dart';

import 'ugc_publisher_liveness.dart';

class UgcLiveSyncTransitions {
  const UgcLiveSyncTransitions._();

  static UgcLiveSyncSettings localFallback(UgcLiveSyncSettings settings) {
    return UgcLiveSyncSettings(
      transport: 'localFolder',
      watchFolder: settings.watchFolder,
      syncServerUrl: settings.syncServerUrl,
      sceneId: settings.sceneId,
      autoConnectOnStart: true,
      maxSnapshotBytes: settings.maxSnapshotBytes,
      debounceMilliseconds: settings.debounceMilliseconds,
    );
  }

  static UgcLiveSyncSettings? connectPublisherSession(
    UgcLiveSyncSettings settings,
    Map<String, Object?> session,
  ) {
    final documentUrl = (session['documentUrl'] as String?)?.trim() ?? '';
    final leaseToken =
        (session['publisherLeaseToken'] as String?)?.trim() ?? '';
    final publisherPid = (session['publisherPid'] as num?)?.toInt() ?? 0;
    if (documentUrl.isEmpty ||
        leaseToken.isEmpty ||
        !UgcPublisherLiveness.isAlive(publisherPid)) {
      return null;
    }
    final sessionSyncUrl = (session['syncUrl'] as String?)?.trim() ?? '';
    final sessionSceneId = (session['sceneId'] as String?)?.trim() ?? '';
    return UgcLiveSyncSettings(
      transport: 'automerge',
      watchFolder: settings.watchFolder,
      editorUrl: settings.editorUrl,
      documentUrl: documentUrl,
      syncServerUrl: sessionSyncUrl.isEmpty
          ? settings.syncServerUrl
          : sessionSyncUrl,
      sceneId: settings.sceneId.isEmpty ? sessionSceneId : settings.sceneId,
      autoConnectOnStart: true,
      maxSnapshotBytes: settings.maxSnapshotBytes,
      debounceMilliseconds: settings.debounceMilliseconds,
    );
  }
}
