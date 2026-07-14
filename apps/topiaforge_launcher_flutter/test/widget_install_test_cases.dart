part of 'widget_test.dart';

void registerInstallConfirmationWidgetTests() {
  testWidgets('package capabilities require explicit install confirmation', (
    tester,
  ) async {
    const manifest = ModManifest(
      schemaVersion: 3,
      id: 'permission.mod',
      name: 'Permission Mod',
      version: '1.0.0',
      author: ModAuthor(name: 'Author'),
      description: 'Permission test.',
      entryAssembly: 'Permission.dll',
      entryType: 'Permission.Mod',
      permissions: ['filesystem', 'network'],
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
          packageUrl: 'file:///permission.topiaforgemod',
          packageSha256: 'verified',
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
  });
}
