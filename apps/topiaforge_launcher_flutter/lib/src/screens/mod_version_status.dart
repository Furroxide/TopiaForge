part of '../screens.dart';

Widget _installedVersionTile(InstalledModVersionStatus status) {
  final invalid = !status.isValid;
  return Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(child: Text(status.version)),
            StatusPill(
              label: status.selected
                  ? invalid
                        ? 'Selected · invalid'
                        : 'Selected'
                  : invalid
                  ? 'Invalid'
                  : 'Installed',
              tone: invalid
                  ? StatusTone.danger
                  : status.selected
                  ? StatusTone.good
                  : StatusTone.neutral,
              icon: invalid ? Icons.error : Icons.inventory_2,
            ),
          ],
        ),
        SelectableText(
          status.packagePath,
          style: const TextStyle(
            color: TopiaForgePalette.mutedText,
            fontSize: 11,
          ),
        ),
        ...status.errors.map(
          (error) => _issueTile(
            LauncherIssue(severity: IssueSeverity.error, message: error),
          ),
        ),
      ],
    ),
  );
}
