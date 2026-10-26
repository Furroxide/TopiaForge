part of 'session_widgets.dart';

/// The first-use disclosure's three paragraphs, in the approved wording.
class PrivacyDisclosureBody extends StatelessWidget {
  const PrivacyDisclosureBody({super.key});

  @override
  Widget build(BuildContext context) => const Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: [
      Text(MultiplayerPrivacyText.disclosureExposure),
      SizedBox(height: 10),
      Text(MultiplayerPrivacyText.disclosureHidden),
      SizedBox(height: 10),
      Text(MultiplayerPrivacyText.disclosureChoice),
    ],
  );
}

/// The two buttons of "Allow a direct connection?", in the approved wording.
/// Cancel, the safe choice, is the primary button and has the focus, so a
/// stray Enter keeps Hide my IP on.
class DirectConnectionActions extends StatelessWidget {
  const DirectConnectionActions({
    super.key,
    required this.onAllow,
    required this.onCancel,
  });

  final VoidCallback onAllow;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) => Wrap(
    alignment: WrapAlignment.end,
    spacing: 8,
    runSpacing: 8,
    children: [
      TextButton(
        key: const Key('direct-connection-allow'),
        onPressed: onAllow,
        child: const Text(MultiplayerPrivacyText.directAllowButton),
      ),
      FilledButton(
        key: const Key('direct-connection-cancel'),
        autofocus: true,
        onPressed: onCancel,
        child: const Text(MultiplayerPrivacyText.directCancelButton),
      ),
    ],
  );
}

/// Asks "Allow a direct connection?" in the approved wording, for one
/// session. True only when the player allowed it; Cancel, Escape and closing
/// the prompt keep Hide my IP on.
Future<bool> showDirectConnectionPrompt(BuildContext context) async {
  final allowed = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      key: const Key('direct-connection-dialog'),
      title: const Text(MultiplayerPrivacyText.directTitle),
      content: const SingleChildScrollView(
        child: Text(MultiplayerPrivacyText.directBody),
      ),
      actions: [
        DirectConnectionActions(
          onAllow: () => Navigator.of(context).pop(true),
          onCancel: () => Navigator.of(context).pop(false),
        ),
      ],
    ),
  );
  return allowed ?? false;
}

/// Where a step of a step rail stands.
enum StepRailStatus { done, current, pending, skipped, failed }

/// One step of a [StepRail].
final class StepRailItem {
  const StepRailItem({
    required this.label,
    required this.status,
    this.detail,
    this.progress,
  });

  final String label;
  final StepRailStatus status;

  /// A line under the step, such as how much is downloaded.
  final String? detail;

  /// How far the step is, from 0 to 1, when that is known.
  final double? progress;
}

/// Steps in order, each saying whether it is done, running, waiting,
/// skipped or where it failed, in words as well as by colour and icon. The
/// running step spins only while animations are on; with reduced motion it
/// shows a still icon.
class StepRail extends StatelessWidget {
  const StepRail({super.key, required this.steps});

  final List<StepRailItem> steps;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    mainAxisSize: MainAxisSize.min,
    children: [
      for (var index = 0; index < steps.length; index++)
        _StepRailRow(item: steps[index], last: index == steps.length - 1),
    ],
  );
}

class _StepRailRow extends StatelessWidget {
  const _StepRailRow({required this.item, required this.last});

  final StepRailItem item;
  final bool last;

  static String statusWord(StepRailStatus status) => switch (status) {
    StepRailStatus.done => 'done',
    StepRailStatus.current => 'in progress',
    StepRailStatus.pending => 'waiting',
    StepRailStatus.skipped => 'not needed',
    StepRailStatus.failed => 'stopped here',
  };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final status = item.status;
    final color = switch (status) {
      StepRailStatus.done => TopiaForgePalette.good,
      StepRailStatus.current => TopiaForgePalette.launchDark,
      StepRailStatus.failed => TopiaForgePalette.danger,
      StepRailStatus.pending ||
      StepRailStatus.skipped => TopiaForgePalette.mutedText,
    };
    final still = MediaQuery.of(context).disableAnimations;
    final Widget icon = switch (status) {
      StepRailStatus.done => Icon(Icons.check_circle, size: 20, color: color),
      StepRailStatus.current when !still => SizedBox.square(
        dimension: 18,
        child: CircularProgressIndicator(strokeWidth: 2.5, color: color),
      ),
      StepRailStatus.current => Icon(Icons.pending, size: 20, color: color),
      StepRailStatus.pending => Icon(
        Icons.radio_button_unchecked,
        size: 20,
        color: color,
      ),
      StepRailStatus.skipped => Icon(
        Icons.remove_circle_outline,
        size: 20,
        color: color,
      ),
      StepRailStatus.failed => Icon(Icons.error, size: 20, color: color),
    };
    final emphasized =
        status == StepRailStatus.current || status == StepRailStatus.failed;
    final label = Text(
      status == StepRailStatus.skipped
          ? '${item.label} (not needed)'
          : item.label,
      style:
          (emphasized ? theme.textTheme.titleSmall : theme.textTheme.bodyMedium)
              ?.copyWith(
                color:
                    status == StepRailStatus.pending ||
                        status == StepRailStatus.skipped
                    ? TopiaForgePalette.mutedText
                    : TopiaForgePalette.text,
              ),
    );
    return Semantics(
      container: true,
      label: '${item.label}, ${statusWord(status)}',
      excludeSemantics: item.detail == null,
      child: IntrinsicHeight(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SizedBox(
              width: 28,
              child: Column(
                children: [
                  SizedBox(height: 22, child: Center(child: icon)),
                  if (!last)
                    Expanded(
                      child: Container(
                        width: 2,
                        color: status == StepRailStatus.done
                            ? TopiaForgePalette.good.withValues(alpha: 0.5)
                            : TopiaForgePalette.surfaceTint,
                      ),
                    ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Padding(
                padding: EdgeInsets.only(top: 2, bottom: last ? 0 : 12),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    label,
                    if (item.detail case final detail?) ...[
                      const SizedBox(height: 2),
                      Text(detail, style: theme.textTheme.bodySmall),
                    ],
                    if (item.progress case final progress?) ...[
                      const SizedBox(height: 6),
                      LinearProgressIndicator(
                        value: progress.clamp(0, 1).toDouble(),
                        minHeight: 6,
                        borderRadius: BorderRadius.circular(3),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// What the canonical capability labels mean, from
/// `docs/PrivacyAndCapabilities.md`. A label that is not here shows as it
/// is.
const capabilityMeanings = <String, String>{
  'network': 'Opens outbound network connections.',
  'remote-ai':
      'Sends inputs to a remote inference service and uses its '
      'response.',
  'player-token':
      "Reads the player's Robotopia sign-in token for an "
      'integration you enable.',
  'microphone': 'Captures audio from your microphone after you choose to.',
  'speech-to-text': 'Sends captured audio to a remote transcription service.',
  'unsafe-native': 'Runs native code with no guard rails.',
};

/// The capabilities a set of mods declares, as chips. Each canonical one
/// says what it means in its tooltip; the sensitive ones stand out. No chip
/// is consent: the player consents to the whole sheet.
class CapabilityChips extends StatelessWidget {
  const CapabilityChips({super.key, required this.capabilities});

  final List<String> capabilities;

  @override
  Widget build(BuildContext context) {
    if (capabilities.isEmpty) {
      return Text(
        'No capabilities declared.',
        style: Theme.of(context).textTheme.bodySmall,
      );
    }
    return Wrap(
      spacing: 6,
      runSpacing: 6,
      children: [
        for (final capability in capabilities)
          StatusPill(
            label: capability,
            tone: capabilityMeanings.containsKey(capability)
                ? StatusTone.warning
                : StatusTone.neutral,
            icon: capabilityMeanings.containsKey(capability)
                ? Icons.privacy_tip_outlined
                : Icons.extension_outlined,
            tooltip: capabilityMeanings[capability],
          ),
      ],
    );
  }
}
