import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_ui/launcher_ui.dart';

/// The approved wording as the document holds it, one line per paragraph
/// and table row, with block quote marks and line breaks inside a paragraph
/// taken out.
String _approvedDocument() {
  final file = File('../../docs/internal/MultiplayerPrivacyText.md');
  final paragraphs = <String>[];
  var current = <String>[];
  for (final raw in file.readAsLinesSync()) {
    final line = raw.startsWith('>') ? raw.substring(1).trim() : raw.trim();
    if (line.isEmpty) {
      if (current.isNotEmpty) paragraphs.add(current.join(' '));
      current = [];
    } else {
      current.add(line);
    }
  }
  if (current.isNotEmpty) paragraphs.add(current.join(' '));
  return paragraphs.join('\n');
}

void main() {
  test('every approved string is the approved document word for word', () {
    final document = _approvedDocument();
    const approved = [
      MultiplayerPrivacyText.hideMyIp,
      MultiplayerPrivacyText.disclosureTitle,
      MultiplayerPrivacyText.disclosureExposure,
      MultiplayerPrivacyText.disclosureHidden,
      MultiplayerPrivacyText.disclosureChoice,
      MultiplayerPrivacyText.disclosureButton,
      MultiplayerPrivacyText.directTitle,
      MultiplayerPrivacyText.directBody,
      MultiplayerPrivacyText.directAllowButton,
      MultiplayerPrivacyText.directCancelButton,
      MultiplayerPrivacyText.voiceTitle,
      MultiplayerPrivacyText.voiceBody,
      MultiplayerPrivacyText.voiceOnButton,
      MultiplayerPrivacyText.voiceNotNowButton,
      MultiplayerPrivacyText.sheetPrivacyHidden,
      MultiplayerPrivacyText.sheetPrivacyDirect,
      MultiplayerPrivacyText.sheetVoiceOff,
      MultiplayerPrivacyText.sheetVoiceOn,
    ];
    for (final text in approved) {
      expect(document, contains(text));
    }
    // Each paragraph of the disclosure and the voice text is a whole
    // paragraph of the document, not part of one.
    final paragraphs = document.split('\n').toSet();
    for (final text in [
      MultiplayerPrivacyText.disclosureExposure,
      MultiplayerPrivacyText.disclosureHidden,
      MultiplayerPrivacyText.disclosureChoice,
      MultiplayerPrivacyText.directBody,
      MultiplayerPrivacyText.voiceBody,
    ]) {
      expect(paragraphs, contains(text));
    }
  });

  Future<Future<bool>> open(
    WidgetTester tester,
    Future<bool> Function(BuildContext context) show,
  ) async {
    late Future<bool> result;
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => result = show(context),
              child: const Text('Open'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();
    return result;
  }

  testWidgets('the disclosure is the approved text and its one button', (
    tester,
  ) async {
    var result = await open(tester, showPrivacyDisclosure);
    for (final text in [
      MultiplayerPrivacyText.disclosureTitle,
      MultiplayerPrivacyText.disclosureExposure,
      MultiplayerPrivacyText.disclosureHidden,
      MultiplayerPrivacyText.disclosureChoice,
    ]) {
      expect(find.text(text), findsOneWidget);
    }
    expect(
      find.descendant(
        of: find.byKey(const Key('privacy-disclosure-dialog')),
        matching: find.byWidgetPredicate(
          (widget) => widget is ButtonStyleButton,
        ),
      ),
      findsOneWidget,
    );
    await tester.tap(find.text(MultiplayerPrivacyText.disclosureButton));
    await tester.pumpAndSettle();
    expect(await result, isTrue);

    result = await open(tester, showPrivacyDisclosure);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(await result, isFalse);
  });

  testWidgets('voice consent never comes from a stray Enter', (tester) async {
    var result = await open(tester, showVoiceConsent);
    expect(find.text(MultiplayerPrivacyText.voiceTitle), findsOneWidget);
    expect(find.text(MultiplayerPrivacyText.voiceBody), findsOneWidget);
    final notNow = tester.widget<TextButton>(
      find.widgetWithText(TextButton, MultiplayerPrivacyText.voiceNotNowButton),
    );
    expect(notNow.autofocus, isTrue);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(await result, isFalse);

    result = await open(tester, showVoiceConsent);
    await tester.tap(find.text(MultiplayerPrivacyText.voiceOnButton));
    await tester.pumpAndSettle();
    expect(await result, isTrue);
  });

  testWidgets('the network notice says what each check opens', (tester) async {
    const notice = NetworkCheckNotice(gamePort: 27250, lanPort: 27251);
    expect(notice.lines.join(' '), contains('Allow private networks only'));
    expect(notice.lines.join(' '), contains('listens on UDP 27251'));
    expect(notice.lines.join(' '), contains('UDP and TCP 27250 and UDP 27251'));
    expect(notice.lines.join(' '), contains('turn LAN discovery off'));
    expect(notice.lines.join(' '), contains('not that anyone can reach it'));
    // Hosting on any free port, a check opens only the LAN discovery port.
    const anyPort = NetworkCheckNotice(gamePort: 0, lanPort: 27251);
    expect(
      anyPort.lines,
      contains(
        'A port check briefly opens UDP 27251 on this computer, then closes '
        'it.',
      ),
    );
    expect(anyPort.lines.join(' '), isNot(contains('TCP')));

    var result = await open(
      tester,
      (context) => notice.show(context, asking: true),
    );
    expect(find.text(NetworkCheckNotice.title), findsOneWidget);
    for (final line in notice.lines) {
      expect(find.text(line), findsOneWidget);
    }
    // Cancel has the focus: Enter goes back without touching the network.
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(await result, isFalse);

    result = await open(
      tester,
      (context) => notice.show(context, asking: true),
    );
    await tester.tap(find.text('Continue'));
    await tester.pumpAndSettle();
    expect(await result, isTrue);

    result = await open(
      tester,
      (context) => notice.show(context, asking: false),
    );
    expect(find.text('Continue'), findsNothing);
    await tester.tap(find.text('Close'));
    await tester.pumpAndSettle();
    expect(await result, isFalse);
  });

  testWidgets('a notice wraps its action under long text at large scale', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(320, 480);
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(() {
      tester.view.reset();
      tester.platformDispatcher.clearTextScaleFactorTestValue();
    });
    var pressed = 0;
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: Scaffold(
          body: SessionNotice(
            message:
                'Your multiplayer settings could not be read just now, so '
                'nothing was changed.',
            tone: StatusTone.danger,
            action: OutlinedButton(
              onPressed: () => pressed++,
              child: const Text('Try again'),
            ),
          ),
        ),
      ),
    );
    expect(tester.takeException(), isNull);
    expect(find.byIcon(Icons.error_outline), findsOneWidget);
    await tester.tap(find.text('Try again'));
    expect(pressed, 1);
  });
}
