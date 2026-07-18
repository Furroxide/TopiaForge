part of 'widget_test.dart';

void registerInstallConfirmationWidgetTests() {
  testWidgets('inbox success reports installed and consumed counts', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(),
      inboxOutcome: PackageInboxInstallOutcome(
        candidateCount: 2,
        installedCount: 1,
        supersededCount: 1,
        consumedCount: 2,
        invalidCount: 0,
        installFailureCount: 0,
        consumptionFailureCount: 0,
      ),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Install Inbox'));
    await tester.pumpAndSettle();

    expect(
      find.text('Installed 1 package(s) and consumed 2 inbox file(s).'),
      findsOneWidget,
    );
  });

  testWidgets('partial inbox outcome surfaces issue with warning severity', (
    tester,
  ) async {
    const issue = LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: 'broken.topiaforgemod',
      message: 'TFINBOX110: broken.topiaforgemod failed safe preflight.',
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(),
      inboxOutcome: PackageInboxInstallOutcome(
        candidateCount: 2,
        installedCount: 1,
        supersededCount: 0,
        consumedCount: 1,
        invalidCount: 1,
        installFailureCount: 0,
        consumptionFailureCount: 0,
        issues: const [issue],
      ),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Install Inbox'));
    await tester.pumpAndSettle();

    final status = find.textContaining('Package inbox partially processed:');
    expect(status, findsOneWidget);
    expect(find.textContaining(issue.message), findsOneWidget);
    expect(tester.widget<Text>(status).style?.color, TopiaForgePalette.warning);
  });

  testWidgets('failed inbox outcome surfaces issue with error severity', (
    tester,
  ) async {
    const issue = LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: 'failed.topiaforgemod',
      message: 'TFINBOX120: atomic install failed; package retained.',
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(),
      inboxOutcome: PackageInboxInstallOutcome(
        candidateCount: 1,
        installedCount: 0,
        supersededCount: 0,
        consumedCount: 0,
        invalidCount: 0,
        installFailureCount: 1,
        consumptionFailureCount: 0,
        issues: const [issue],
      ),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Install Inbox'));
    await tester.pumpAndSettle();

    final status = find.textContaining('Package inbox failed:');
    expect(status, findsOneWidget);
    expect(find.textContaining(issue.message), findsOneWidget);
    expect(tester.widget<Text>(status).style?.color, TopiaForgePalette.danger);
  });

  testWidgets('package capabilities require explicit install confirmation', (
    tester,
  ) async {
    const manifest = ModManifest(
      schemaVersion: 4,
      id: 'permission.mod',
      name: 'Permission Mod',
      version: '1.0.0',
      author: ModAuthor(name: 'Author'),
      description: 'Permission test.',
      entryAssembly: 'Permission.dll',
      entryType: 'Permission.Mod',
      capabilities: ['filesystem', 'network'],
    );
    const plan = PackageInstallPlan(
      manifest: manifest,
      issues: [],
      dependenciesToInstall: [],
      optionalDependenciesMissing: [],
      conflictingMods: [],
      packageSha256: 'verified',
      requiredPermissions: ['filesystem', 'network'],
      installActions: [
        PackageInstallAction(
          modId: 'permission.mod',
          name: 'Permission Mod',
          version: '1.0.0',
          expectedManifest: manifest,
          packageUrl: 'file:///permission.topiaforgemod',
          packageSha256: 'verified',
          sourceId: 'official',
          root: true,
        ),
      ],
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(),
      packageInstallPlan: plan,
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    tester
        .element(find.byType(LauncherShell))
        .read<LauncherBloc>()
        .add(const PackagePreviewRequested('/tmp/permission.topiaforgemod'));
    await tester.pumpAndSettle();

    expect(find.text('Declared capabilities'), findsOneWidget);
    expect(find.text('filesystem, network'), findsOneWidget);
    await tester.ensureVisible(find.text('Install Plan'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Install Plan'));
    await tester.pumpAndSettle();

    expect(find.text('Install Permission Mod?'), findsOneWidget);
    expect(
      find.textContaining('Declared runtime capabilities: filesystem, network'),
      findsOneWidget,
    );
    expect(repository.installPackageCount, 0);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(repository.installPackageCount, 0);

    await tester.ensureVisible(find.text('Install Plan'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Install Plan'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Install'));
    await tester.pumpAndSettle();
    expect(repository.installPackageCount, 1);
    expect(repository.lastInstallSourceId, 'official');
  });
}
