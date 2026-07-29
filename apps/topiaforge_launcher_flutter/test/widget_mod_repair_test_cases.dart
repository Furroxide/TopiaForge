part of 'widget_test.dart';

void registerModRepairTests() {
  const damagedVersion = InstalledModVersionStatus(
    version: '0.9.0',
    packagePath: r'C:\Games\Robotopia\BepInEx\TopiaForge\timer.mod\0.9.0',
    errors: ['Receipt mismatch.'],
    selected: false,
    repairable: true,
  );
  const selectedVersion = InstalledModVersionStatus(
    version: '1.0.0',
    packagePath: r'C:\Games\Robotopia\BepInEx\TopiaForge\timer.mod\1.0.0',
    errors: [],
    selected: true,
  );

  test('repair event refreshes and keeps the repaired mod selected', () async {
    final repository = _FakeLauncherRepository(
      snapshot: _updateSnapshot(
        installedVersions: const [selectedVersion, damagedVersion],
      ),
      repairedSnapshot: _updateSnapshot(),
    );
    final bloc = LauncherBloc(repository)..add(const LauncherStarted());
    await bloc.stream.firstWhere((state) => !state.isBusy);

    final repairedState = bloc.stream.firstWhere(
      (state) =>
          state.statusMessage == 'Repaired and revalidated Timer Mod 0.9.0.',
    );
    bloc.add(const SelectedModRepairRequested());

    final result = await repairedState;
    expect(repository.repairInstalledModCount, 1);
    expect(repository.repairRequest?.id, 'timer.mod');
    expect(repository.repairRequest?.repairableVersion, '0.9.0');
    expect(result.selectedMod?.id, 'timer.mod');
    expect(result.selectedMod?.errors, isEmpty);
    await bloc.close();
  });

  testWidgets('invalid selected mod can be repaired and revalidated', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(
      snapshot: _updateSnapshot(
        installedVersions: const [selectedVersion, damagedVersion],
      ),
      repairedSnapshot: _updateSnapshot(),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    expect(find.text('Repair / Reinstall'), findsOneWidget);
    await tester.tap(find.text('Repair / Reinstall'));
    await tester.pumpAndSettle();

    expect(find.text('Repair Timer Mod 0.9.0?'), findsOneWidget);
    expect(repository.repairInstalledModCount, 0);
    await tester.tap(find.widgetWithText(FilledButton, 'Repair'));
    await tester.pumpAndSettle();

    expect(repository.repairInstalledModCount, 1);
    expect(find.text('Repair / Reinstall'), findsNothing);
    expect(
      find.text('Repaired and revalidated Timer Mod 0.9.0.'),
      findsOneWidget,
    );
  });

  testWidgets('mod detail shows every installed version and its errors', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(
          snapshot: _updateSnapshot(
            installedVersions: const [selectedVersion, damagedVersion],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    final detailScrollable = find
        .descendant(
          of: find.byKey(const Key('mod-detail-list')),
          matching: find.byType(Scrollable),
        )
        .first;
    await tester.scrollUntilVisible(
      find.text('0.9.0'),
      120,
      scrollable: detailScrollable,
    );
    expect(find.text('0.9.0'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Receipt mismatch.'),
      120,
      scrollable: detailScrollable,
    );
    expect(find.text('Receipt mismatch.'), findsOneWidget);
  });

  testWidgets('valid selected mod does not show a repair action', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _updateSnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    expect(find.text('Repair / Reinstall'), findsNothing);
  });
}
