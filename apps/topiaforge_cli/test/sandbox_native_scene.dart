import 'dart:convert';

/// A deliberately synthetic Sandbox scene model used only to build valid v2
/// transcript facts for verifier tests. It never represents actual execution.
///
/// Each step mutates the running facts so the produced `after` snapshot
/// satisfies exactly one recipe postcondition relative to the previous
/// snapshot; tests then mutate a single value to flip a scenario.
final class SandboxScene {
  final int uiWidth = 1920, uiHeight = 1080;
  String worldSessionId = 'world-session-1';
  String sessionPhase = 'Running';
  String activeHostId = '';
  bool windowVisible = false;
  String hudText = 'SESSION ACTIVE';
  int creatorSessionCount = 0;
  int creatorEditLeaseCount = 0;
  int robotEditLeaseCount = 0;
  int conversationCount = 0;
  int interactionCount = 0;
  int playerControlLeaseCount = 0;
  int routingHostCount = 1;
  int hostCount = 1,
      ownerCanvasCount = 2,
      totalCanvasCount = 3,
      themeSubscriberCount = 1,
      cursorLeaseCount = 0,
      dismissScopeCount = 0;
  final List<Map<String, Object?>> sandboxContent = [];
  final List<Map<String, Object?>> nativeRobots = [];
  int nextInstanceId = 1000;
  int? selectedId;
  Map<String, Object?>? borrowedRobot;
  final String borrowedRosterId = 'robot-native:robot-scene:9001';
  bool rosterFocused = false;
  bool borrowedSelected = false;
  String focusedNode = '';
  int hackedPersonalitySeq = 0;
  bool highContrast = false;
  double uiScale = 1.0;
  bool reducedMotion = false;
  double motionIntensity = 1.0;
  bool competingRegistered = false;
  int competingOpenCalls = 0;
  bool controlCuePlaying = false;
  int controlRobotInstanceId = 0;
  bool projectRunning = false;
  int undoDepth = 0;
  int? controllerInstanceId = 7777;
  bool borrowedRobotDestroyed = false;
  int graphPlayingAudioCount = 0;
  List<int> graphAudioIds = [];
  List<int> audioSourceIds = [];
  List<int> personalityAssetIds = [];
  List<Map<String, Object?>> interactions = [];
  List<num> playerPosition = [0, 0, 0];
  List<num> playerAim = [0, 0, 1];
  bool aimFocused = false;
  List<Map<String, Object?>> toasts = [];
  double hideWorkbenchHeight = 30;
  bool prepared = true;
  List<int> cycleGraphAudioIds = [];
  Set<int> pooledAudioIds = {};
  bool? stopProjectEnabled;
  bool aimUnavailable = false;
  String failedOperationCode = '';
  List<String> extraUnavailableReasons = [];
  // Fault-injection hooks (test-only). audioFault: '' | 'playing' | 'named' |
  // 'undescribed' injects a graph-audio release fault at the cleanup barrier.
  String audioFault = '';
  final Set<String> dropFacts = {};
  final Map<String, Object?> factOverrides = {};
  bool lowContrastWidget = false;
  bool hideStatusCaption = false;
  final String projectId = 'sandbox-acceptance-graph';
  final List<Map<String, Object?>> catalog = [
    {
      'rowId': 'content:dev.topiaforge.sandbox-acceptance:prop',
      'contentId': 'dev.topiaforge.sandbox-acceptance:prop',
      'displayName': 'Acceptance prop',
      'kind': 'Prop',
      'transformCapabilities': 7,
    },
    {
      'rowId': 'content:dev.topiaforge.sandbox-acceptance:character',
      'contentId': 'dev.topiaforge.sandbox-acceptance:character',
      'displayName': 'Acceptance character',
      'kind': 'Character',
      'transformCapabilities': 1,
    },
  ];
  final List<Map<String, Object?>> robotCatalog = [
    {
      'rowId': 'robotkit:acceptance',
      'displayName': 'Acceptance bot',
      'kind': 'Robot',
      'transformCapabilities': 7,
    },
  ];
  late List<String> catalogListRows = allRowIds();
  bool spawnEnabled = true;
  String catalogKindValue = 'All content';
  String catalogSearchValue = '';

  List<String> allRowIds() => [
    ...catalog.map((e) => e['rowId']! as String),
    ...robotCatalog.map((e) => e['rowId']! as String),
  ];
  List<String> get catalogIds => [...catalog.map((e) => e['rowId']! as String)];
  List<Map<String, Object?>> get catalogSources => [
    {
      'id': 'dev.topiaforge.sandbox-acceptance',
      'displayName': 'Acceptance',
      'state': 'Loaded',
      'entryCount': catalog.length,
    },
    {
      'id': 'robotopia.vehicles',
      'displayName': 'Vehicles',
      'state': 'Unavailable',
      'entryCount': 0,
    },
  ];

  static const screenSteps = {
    'open',
    'reopen',
    'hide-f5',
    'hide-close',
    'end-session',
    'stop-world-session',
  };

  Map<String, Object?>? originalBorrowed;

  static List<num> identityTransform() => [0, 0, 0, 0, 0, 0, 1, 1, 1, 1];

  /// Observer-style unavailable reasons carried on every reply.
  List<String> get unavailableReasons => [
    if (failedOperationCode.isNotEmpty)
      'fixture-operation-failed:$failedOperationCode',
    ...extraUnavailableReasons,
  ];

  /// Live audio sources. Released graph hosts stay alive renamed and silent,
  /// as the shipping OwnerAudioService pools them; `audioFault` injects a
  /// release fault (still playing, still named after the cue, or undescribed).
  List<Map<String, Object?>> _audioSources() => [
    for (final id in audioSourceIds)
      if (audioFault != 'undescribed' || !cycleGraphAudioIds.contains(id))
        {
          'instanceId': id,
          'name': pooledAudioIds.contains(id) && audioFault == ''
              ? 'TopiaForge.Audio.Pooled'
              : 'TopiaForge.Audio.sandbox-acceptance-graph',
          'playing': audioFault == 'playing' || !pooledAudioIds.contains(id),
        },
  ];

  int addEntity(List<Map<String, Object?>> pool, {List<num>? transform}) {
    final id = ++nextInstanceId;
    pool.add({
      'instanceId': id,
      'sceneHandle': 5,
      'alive': true,
      'transform': transform ?? identityTransform(),
      'scope': 'sandbox-owner',
    });
    return id;
  }

  void prepareBorrowed() {
    borrowedRobot = {
      'instanceId': 9001,
      'sceneHandle': 7,
      'transform': identityTransform(),
      'brain': {
        'state': 'Standby',
        'initialState': 'Standby',
        'llmDisabled': true,
        'behaviorTrees': <Object?>[],
        'hackedPersonalityId': 0,
        'hackedPersonalityFingerprint': 'none',
      },
    };
  }

  Map<String, Object?> _brain() =>
      (borrowedRobot!['brain']! as Map).cast<String, Object?>();

  void editBorrowedPersonality() {
    hackedPersonalitySeq++;
    final id = 5000 + hackedPersonalitySeq;
    personalityAssetIds = [...personalityAssetIds, id];
    _brain()['hackedPersonalityId'] = id;
    _brain()['hackedPersonalityFingerprint'] = 'fp-$hackedPersonalitySeq';
  }

  /// Snapshots the current scene as a full facts map, including widgets.
  Map<String, Object?> snapshot() {
    final facts = <String, Object?>{
      'targetId': 'io.github.furroxide.topiaforge.sandbox.creator.menu',
      'worldSessionId': worldSessionId,
      'sessionPhase': sessionPhase,
      'sessionSequence': 1,
      'gamemodeId': 'creator',
      'worldId': 'world',
      'activeHostId': activeHostId,
      'catalogIds': catalogIds,
      'catalog': catalog,
      'robotCatalog': robotCatalog,
      'catalogSources': catalogSources,
      'sandboxContent': _clone(sandboxContent),
      'nativeRobots': _clone(nativeRobots),
      'interactions': _clone(interactions),
      'interactionCount': interactionCount,
      'playerControlLeaseCount': playerControlLeaseCount,
      'robotTypes': <Object?>[],
      'playerPosition': [...playerPosition],
      'playerAim': [...playerAim],
      'fixtureObjects': <Object?>[],
      'ownedObjectCount': sandboxContent.length,
      'createdObjectCount': nextInstanceId - 1000,
      'disposedObjectCount': 0,
      'cleanupErrors': <Object?>[],
      'nativeProps': [
        for (final e in sandboxContent)
          {
            'instanceId': e['instanceId'],
            'sceneHandle': e['sceneHandle'],
            'active': true,
            'transform': e['transform'],
          },
      ],
      'nativeCleanupPendingObjects': 0,
      'borrowedRosterId': borrowedRosterId,
      'projectId': projectId,
      'borrowedRobot': borrowedRobot == null ? null : _cloneMap(borrowedRobot!),
      'mutationSafetyState': 'Unavailable',
      'persistenceIsolationAvailable': false,
      'creatorSessionCount': creatorSessionCount,
      'creatorEditLeaseCount': creatorEditLeaseCount,
      'robotEditLeaseCount': robotEditLeaseCount,
      'conversationCount': conversationCount,
      'routingHostCount': routingHostCount,
      'graphPlayingAudioCount': graphPlayingAudioCount,
      'graphAudioSources': [
        for (final id in graphAudioIds)
          {
            'instanceId': id,
            'playing': true,
            'clipId': id + 1,
            'samplePosition': 0,
          },
      ],
      'toasts': _clone(toasts),
      'accessibility': {
        'highContrast': highContrast,
        'uiScale': uiScale,
        'reducedMotion': reducedMotion,
        'motionIntensity': motionIntensity,
      },
      'competingHost': {
        'registered': competingRegistered,
        'canOpenCalls': competingRegistered ? 3 : 0,
        'openCalls': competingOpenCalls,
        'closeCalls': 0,
      },
      'controlCuePlaying': controlCuePlaying,
      'controllerInstanceId': controllerInstanceId,
      'projectRunning': projectRunning,
      'undoDepth': undoDepth,
      'controlRobotInstanceId': controlRobotInstanceId,
      'borrowedRobotDestroyed': borrowedRobotDestroyed,
      'personalityAssetIds': [...personalityAssetIds],
      'audioSourceIds': [...audioSourceIds],
      'audioSources': _audioSources(),
      'graphAudioIds': [...cycleGraphAudioIds],
      'previewedPersonalityId': hackedPersonalitySeq == 0
          ? 0
          : 5000 + hackedPersonalitySeq,
      'aimToGraphProp': aimUnavailable
          ? {
              'available': false,
              'yawDegrees': null,
              'pitchDegrees': null,
              'distance': null,
              'focused': false,
            }
          : {
              'available': true,
              'yawDegrees': aimFocused ? 0.0 : 12.0,
              'pitchDegrees': aimFocused ? 0.0 : 6.0,
              'distance': 3.0,
              'focused': aimFocused,
            },
      'focusedInteraction': {'available': true, 'entityInstanceId': null},
      'prepared': prepared,
      'operationAccepted': failedOperationCode.isEmpty,
      'operationErrorCode': failedOperationCode,
      'scenarioState': 'observed',
      'completedCycles': 99,
      'ui': {
        'width': uiWidth,
        'height': uiHeight,
        'hostCount': hostCount,
        'ownerCanvasCount': ownerCanvasCount,
        'totalCanvasCount': totalCanvasCount,
        'themeSubscriberCount': themeSubscriberCount,
        'cursorLeaseCount': cursorLeaseCount,
        'dismissScopeCount': dismissScopeCount,
        'widgets': _widgets(),
      },
    };
    facts.addAll(factOverrides);
    for (final key in dropFacts) {
      facts.remove(key);
    }
    return facts;
  }

  List<Map<String, Object?>> _widgets() {
    final widgets = <Map<String, Object?>>[
      _widget('sandbox-creator-hud', 'hud', kind: 'label', text: hudText),
      if (!hideStatusCaption)
        _widget(
          'sandbox-creator-hud',
          'status-caption',
          kind: 'label',
          text: 'Sandbox isolation active.',
        ),
      if (lowContrastWidget)
        (_widget('sandbox-creator-window', 'low-contrast', kind: 'label')
          ..['foreground'] = '#808080'
          ..['background'] = '#8a8a8a'),
    ];
    for (final node in const [
      'hide-workbench',
      'catalog-search',
      'catalog-kind',
      'spawn-selected',
      'duplicate-selected',
      'nudge-up',
      'refresh-native',
      'persona-name',
      'persona-instructions',
      'apply-personality',
      'brain-dormant',
      'load-project',
      'run-project',
      'stop-project',
      'end-session',
      'rotation-y',
      'rotation-w',
      'apply-transform',
      'scale-x',
      'scale-y',
      'scale-z',
      'remove-selected',
      'undo-last',
      r'$close',
      r'$scroll-0',
      r'$scroll-1',
      r'$scroll-2',
    ]) {
      widgets.add(
        _widget(
          'sandbox-creator-window',
          node,
          kind: node.startsWith(r'$scroll') ? 'scroll' : 'button',
          enabled: node == 'spawn-selected'
              ? spawnEnabled
              : node == 'stop-project'
              ? (stopProjectEnabled ?? projectRunning)
              : true,
          focused: node == focusedNode,
          width: node.startsWith(r'$scroll') ? 400 : 120,
          height: node == 'hide-workbench'
              ? hideWorkbenchHeight
              : node.startsWith(r'$scroll')
              ? 400
              : 24,
          value: node == 'catalog-kind'
              ? catalogKindValue
              : node == 'catalog-search'
              ? catalogSearchValue
              : '',
        ),
      );
    }
    widgets.add(_widget('sandbox-creator-window', 'catalog-list'));
    for (final row in catalogListRows) {
      widgets.add(_widget('sandbox-creator-window', 'catalog-list/$row'));
    }
    widgets.add(_widget('sandbox-creator-window', 'roster-list'));
    widgets.add(
      _widget(
        'sandbox-creator-window',
        'roster-list/$borrowedRosterId',
        focused: focusedNode == 'roster-list/$borrowedRosterId',
      ),
    );
    widgets.add(_widget('sandbox-creator-window', 'project-list'));
    widgets.add(_widget('sandbox-creator-window', 'project-list/$projectId'));
    widgets.add(_widget(r'$modal', 'confirm'));
    return widgets;
  }

  Map<String, Object?> _widget(
    String surface,
    String node, {
    String kind = 'button',
    String text = '',
    bool enabled = true,
    bool focused = false,
    double width = 120,
    double height = 24,
    String value = '',
  }) => {
    'surfaceId': surface,
    'nodeId': node,
    'kind': kind,
    'text': text,
    'style': 'default',
    'x': 10,
    'y': 10,
    'width': width,
    'height': height,
    'visible': surface == 'sandbox-creator-hud' ? true : windowVisible,
    'enabled': enabled,
    'focused': focused,
    'clipped': false,
    'highContrast': highContrast,
    'reducedMotion': reducedMotion,
    'uiScale': uiScale,
    'motionIntensity': motionIntensity,
    'selected': false,
    'value': value,
    // High-contrast-safe palette: black on white is ~21:1.
    'foreground': '#000000',
    'background': '#ffffff',
  };

  static List<Map<String, Object?>> _clone(List<Map<String, Object?>> value) =>
      (jsonDecode(jsonEncode(value)) as List).cast<Map<String, Object?>>();
  static Map<String, Object?> _cloneMap(Map<String, Object?> value) =>
      (jsonDecode(jsonEncode(value)) as Map).cast<String, Object?>();
  static Map<String, Object?> cloneMap(Map<String, Object?> value) =>
      _cloneMap(value);
}
