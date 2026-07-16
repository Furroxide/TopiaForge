import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_ui/launcher_ui.dart';

void main() {
  testWidgets('TopiaForgeLogo renders the pixel wordmark accessibly', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: const Scaffold(body: TopiaForgeLogo()),
      ),
    );

    final logo = tester.widget<TopiaForgeLogo>(find.byType(TopiaForgeLogo));
    final raster = tester.widget<Image>(
      find.descendant(
        of: find.byType(TopiaForgeLogo),
        matching: find.byType(Image),
      ),
    );
    final asset = raster.image as AssetImage;

    expect(logo.height, 36);
    expect(raster.height, 36);
    expect(raster.filterQuality, FilterQuality.none);
    expect(raster.isAntiAlias, isFalse);
    expect(raster.semanticLabel, 'TopiaForge');
    expect(asset.assetName, TopiaForgeBrandAssets.logo);
    expect(asset.package, TopiaForgeBrandAssets.package);
  });

  testWidgets('StatusPill renders label', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: const Scaffold(
          body: StatusPill(label: 'Ready', tone: StatusTone.good),
        ),
      ),
    );

    expect(find.text('Ready'), findsOneWidget);
  });

  testWidgets('StatusPill exposes optional tooltip', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: const Scaffold(
          body: StatusPill(
            label: 'Restart',
            tone: StatusTone.warning,
            tooltip: 'Relaunch TopiaForge to apply pending changes.',
          ),
        ),
      ),
    );

    expect(
      find.byTooltip('Relaunch TopiaForge to apply pending changes.'),
      findsOneWidget,
    );
  });
}
