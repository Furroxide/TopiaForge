import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_launch_options.dart';
import 'package:topiaforge/src/cli_launch_workflow.dart';

void main() {
  late _Repository repository;
  late List<String> messages;
  setUp(() {
    repository = _Repository();
    messages = [];
  });
  tearDown(() => repository.controller.close());
  Future<int> launch({CliLaunchOptions? options, Future<void>? cancelled}) =>
      runCliLaunch(
        repository: repository,
        install: repository.install,
        profile: repository.profile,
        options:
            options ??
            const CliLaunchOptions(waitTimeout: Duration(milliseconds: 15)),
        write: messages.add,
        cancelled: cancelled,
      );

  test(
    'process start without acknowledgement is unconfirmed, not success',
    () async {
      expect(await launch(), 3);
      expect(messages.join(' '), contains('unconfirmed'));
      expect(repository.controller.hasListener, isFalse);
    },
  );
  test(
    'subscribes before spawn and accepts fast correlated acknowledgement',
    () async {
      repository.onLaunch = () =>
          repository.controller.add(repository.succeeded());
      expect(await launch(), 0);
      expect(messages.join(' '), contains('Main menu confirmed'));
    },
  );
  test('ignores foreign receipts and returns runtime failure', () async {
    repository.onLaunch = () {
      repository.controller.add(repository.succeeded(requestId: 'foreign'));
      repository.controller.add(repository.failed());
    };
    expect(await launch(), 1);
    expect(messages.join(' '), contains('missing native scene'));
  });
  test(
    'cancellation before preflight completion prevents process creation',
    () async {
      expect(await launch(cancelled: Future<void>.value()), 130);
      expect(repository.launches, 0);
    },
  );
  test('preview refusal prevents process creation', () async {
    repository.blocked = true;
    expect(await launch(), 1);
    expect(repository.launches, 0);
    expect(messages.join(' '), contains('Repair selection'));
  });
  test(
    'wrong profile revision and changed package digest cannot acknowledge',
    () async {
      repository.onLaunch = () {
        repository.controller.add(repository.succeeded(revision: 99));
        repository.controller.add(repository.succeeded(digest: 'changed'));
      };
      expect(await launch(), 3);
    },
  );
  test(
    'explicit one-shot main menu preserves saved target selection',
    () async {
      final saved = repository.profile.launchSelection;
      expect(
        await launch(
          options: const CliLaunchOptions(
            selectionOverride: LaunchSelection.mainMenu(),
            waitForAcknowledgement: false,
          ),
        ),
        0,
      );
      expect(repository.selectionReceived, const LaunchSelection.mainMenu());
      expect(repository.profile.launchSelection, saved);
      expect(messages.join(' '), contains('unconfirmed'));
    },
  );
  test('caller cancellation stops waiting and releases subscription', () async {
    final cancelled = Completer<void>();
    repository.onLaunch = cancelled.complete;
    expect(await launch(cancelled: cancelled.future), 130);
    expect(repository.controller.hasListener, isFalse);
  });
  test(
    'receipt latest activity retains acknowledgement before late subscription',
    () async {
      repository.latest = repository.succeeded();
      expect(await launch(), 0);
      expect(messages.join(' '), contains('Main menu confirmed'));
    },
  );
}

class _Repository implements LauncherRepository {
  final controller = StreamController<LaunchActivity>.broadcast(sync: true);
  final profile = LauncherProfile.defaultProfile().copyWith(
    revision: 2,
    launchSelection: LaunchSelection.target(
      LaunchRequest(targetId: 'test.target'),
    ),
  );
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  bool blocked = false;
  int launches = 0;
  void Function()? onLaunch;
  LaunchSelection? selectionReceived;
  LaunchActivity? latest;
  LaunchActivity base({
    String requestId = 'request-one',
    int revision = 2,
    String digest = 'digest',
  }) => LaunchActivity(
    requestId: requestId,
    profileId: profile.id,
    profileRevision: revision,
    installIdentity: 'install',
    packageDigest: digest,
    command: 'main-menu',
    unconfirmed: true,
  );
  LaunchActivity succeeded({
    String requestId = 'request-one',
    int revision = 2,
    String digest = 'digest',
  }) => base(requestId: requestId, revision: revision, digest: digest)
      .applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: requestId,
          command: 'main-menu',
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
        ),
      );
  LaunchActivity failed() => base().applyOutcome(
    LaunchOutcome(
      kind: 'launch',
      requestId: 'request-one',
      command: 'main-menu',
      sequence: 1,
      status: 'failed',
      phase: 'idle',
      error: LaunchOperationError(
        code: 'notFound',
        message: 'missing native scene',
      ),
    ),
  );
  @override
  Stream<LaunchActivity> get launchActivities => controller.stream;
  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async => LaunchPreview(
    profileId: profile.id,
    profileRevision: profile.revision,
    installIdentity: 'install',
    packageDigest: 'digest',
    requestedSelection: selectionOverride ?? profile.launchSelection,
    effectiveSelection: const LaunchSelection.mainMenu(),
    issues: blocked
        ? const [
            LauncherIssue(
              severity: IssueSeverity.error,
              message: 'Repair selection',
            ),
          ]
        : const [],
  );
  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    launches++;
    selectionReceived = selectionOverride;
    onLaunch?.call();
    return LaunchResult(
      started: true,
      message: 'Process created.',
      requestId: 'request-one',
      latestActivity: latest ?? base(),
    );
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
