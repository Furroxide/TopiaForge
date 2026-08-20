part of 'topiaforge.dart';

/// Top-level command names, mirroring the dispatch switch in `_TopiaForgeCli.run`.
/// Used for did-you-mean suggestions on unknown commands.
const _commands = [
  'help',
  'new',
  'mod',
  'migrate-manifest',
  'check',
  'acceptance',
  'add',
  'remove',
  'list',
  'resolve',
  'restore',
  'dev',
  'pack',
  'install',
  'launch',
  'restart',
  'dev-install',
  'doctor',
  'compat',
  'setup',
  'ugc',
  'world',
  'projects',
  'unity',
  'updates',
  'registry',
  'release',
  'launcher',
];

/// Shared remediation message for commands that need a detected game install.
const _noInstallRemedy =
    'Robotopia install was not detected. Set ROBOTOPIA_GAME_DIR (or select '
    'the game folder in the launcher), or run `topiaforge doctor` to diagnose.';

/// Two-row dynamic-programming Levenshtein distance, for did-you-mean.
int _editDistance(String a, String b) {
  if (a == b) return 0;
  if (a.isEmpty) return b.length;
  if (b.isEmpty) return a.length;
  var previous = List<int>.generate(b.length + 1, (i) => i);
  final current = List<int>.filled(b.length + 1, 0);
  for (var i = 0; i < a.length; i++) {
    current[0] = i + 1;
    for (var j = 0; j < b.length; j++) {
      final substitution = previous[j] + (a[i] == b[j] ? 0 : 1);
      current[j + 1] = [
        previous[j + 1] + 1,
        current[j] + 1,
        substitution,
      ].reduce((x, y) => x < y ? x : y);
    }
    previous = List<int>.of(current);
  }
  return previous[b.length];
}

/// Closest known command for [input], or null when nothing is plausibly near.
String? _suggestCommand(String input) {
  final lower = input.toLowerCase();
  String? best;
  var bestDistance = 3; // suggest only when distance <= 2
  for (final command in _commands) {
    if (command.startsWith(lower)) return command;
    final distance = _editDistance(lower, command);
    if (distance < bestDistance) {
      bestDistance = distance;
      best = command;
    }
  }
  return best;
}

/// The `topiaforge help` text (split out so topiaforge.dart stays under the file-size cap).
extension _HelpCommand on _TopiaForgeCli {
  void _printHelp() {
    stdout.writeln('TopiaForge CLI — build, package, and run TopiaForge mods.');
    stdout.writeln('');
    stdout.writeln('Getting started:');
    stdout.writeln(
      '  topiaforge setup                       Audit the toolchain and apply safe fixes.',
    );
    stdout.writeln(
      '  topiaforge doctor [--strict]           Read-only toolchain, project, and game-compat audit.',
    );
    stdout.writeln(
      '  topiaforge new mod <id> [--template t] [--license SPDX --license-file path] Scaffold a mod project.',
    );
    stdout.writeln(
      '  topiaforge list templates              List the available mod templates.',
    );
    stdout.writeln('');
    stdout.writeln('Project & manifest:');
    stdout.writeln(
      '  topiaforge mod show                    Print the current topiaforge.mod.json.',
    );
    stdout.writeln(
      '  topiaforge mod set <field> <value>     Update a manifest field (validated on write).',
    );
    stdout.writeln(
      '  topiaforge mod add|remove <kind> <v>   Add/remove capability, dependency, conflict, gamemode, ...',
    );
    stdout.writeln(
      '  topiaforge mod add|remove <module>     Couple a V1 module PackageReference with its runtime dependency.',
    );
    stdout.writeln(
      '  topiaforge mod sync multiplayer        Refresh generated wire IDs and schema hashes in the checked-in lock.',
    );
    stdout.writeln(
      '  topiaforge mod bump [major|minor|patch]  Increment the manifest version.',
    );
    stdout.writeln(
      '  topiaforge migrate-manifest            Convert a schema-V3 or retired V4 manifest to V5.',
    );
    stdout.writeln(
      '  topiaforge check project [path]        Validate a developer project.',
    );
    stdout.writeln(
      '  topiaforge check package <path>        Validate a mod folder or .topiaforgemod (--sha256, --entry, --resolve).',
    );
    stdout.writeln(
      '  topiaforge check scaffold <path>       Verify an extracted-release scaffold, SDK locks, and optional install receipt.',
    );
    stdout.writeln('');
    stdout.writeln('Packages & sources:');
    stdout.writeln(
      '  topiaforge add source <id> <url>       Register a package source.',
    );
    stdout.writeln(
      '  topiaforge add package <id[@range]>    Add a dependency to the project manifest.',
    );
    stdout.writeln(
      '  topiaforge remove package <id>         Remove a dependency.',
    );
    stdout.writeln(
      '  topiaforge list sources                List registered package sources.',
    );
    stdout.writeln(
      '  topiaforge resolve [--prerelease]      Compute the dependency plan and write topiaforge.lock.json.',
    );
    stdout.writeln(
      '  topiaforge restore [--prerelease]      Seed the SDK feed, restore NuGet/mod packages, and write lock files.',
    );
    stdout.writeln('');
    stdout.writeln('Build & run:');
    stdout.writeln(
      '  topiaforge dev [--no-launch --no-tail] Restore, build, test, pack, validate, and install.',
    );
    stdout.writeln(
      '  topiaforge pack [--output dir]         Build and package the current mod project.',
    );
    stdout.writeln(
      '  topiaforge pack --all                  Pack non-DevTool first-party mods (--include-dev-mods to include all).',
    );
    stdout.writeln(
      '  topiaforge install [package] [--game-dir p] Install a .topiaforgemod into Robotopia.',
    );
    stdout.writeln(
      '  topiaforge dev-install [--game-dir p]  Install the loader + dev mods into the game.',
    );
    stdout.writeln(
      '  topiaforge launch [--game-dir p]       Launch Robotopia.',
    );
    stdout.writeln(
      '  topiaforge restart                     Restart Robotopia.',
    );
    stdout.writeln(
      '  topiaforge compat [--json]             Resolve declared game bindings against the install.',
    );
    stdout.writeln(
      '  topiaforge acceptance run              Run the instrumented Robotopia V1 live gate.',
    );
    stdout.writeln('');
    stdout.writeln('UGC live-sync:');
    stdout.writeln(
      '  topiaforge ugc setup                   Configure live-sync (transport, watch folder).',
    );
    stdout.writeln(
      '  topiaforge ugc dev [--project p]       One-command UGC dev loop (watch + deploy).',
    );
    stdout.writeln(
      '  topiaforge ugc publish --file <p>      Publish a UGC project.',
    );
    stdout.writeln(
      '  topiaforge ugc watch <folder>          Watch a folder and sync changes into the game.',
    );
    stdout.writeln(
      '  topiaforge ugc status                  Show live-sync status.',
    );
    stdout.writeln(
      '  topiaforge ugc cleanup                 Stop live sync and clear transient state.',
    );
    stdout.writeln(
      '  topiaforge ugc go-live                 Promote the current UGC session.',
    );
    stdout.writeln('');
    stdout.writeln('Unity & worlds:');
    stdout.writeln(
      '  topiaforge new unity-world <name>      Scaffold a Unity world project paired with a mod.',
    );
    stdout.writeln(
      '  topiaforge world link --project <p> --mod <m>  Pair a Unity project with a world mod.',
    );
    stdout.writeln(
      '  topiaforge world build [--project p]   Build the world asset bundle via Unity.',
    );
    stdout.writeln(
      '  topiaforge world play [--project p]    Build, install, and launch the world mod.',
    );
    stdout.writeln(
      '  topiaforge unity <subcommand>          Unity package management (new-package, resolve, add, remove,',
    );
    stdout.writeln(
      '                                        list, repos, add-repo, pack-packages).',
    );
    stdout.writeln(
      '  topiaforge unity pack-packages --package <dir> ...  Build a community VPM listing.',
    );
    stdout.writeln(
      '  topiaforge unity build-ui-bundle       Rebuild the embedded TopiaForge brand bundle (repo maintainers).',
    );
    stdout.writeln(
      '  topiaforge projects list|add|remove|open  Manage registered Unity projects.',
    );
    stdout.writeln('');
    stdout.writeln('Publish & registry:');
    stdout.writeln(
      '  topiaforge registry add-entry <pkg> --url <url>  Create an entry for a local or self-hosted registry.',
    );
    stdout.writeln(
      '  topiaforge registry index ...          Build a local or self-hosted registry index.json.',
    );
    stdout.writeln(
      '  topiaforge registry validate           Validate local/self-hosted entries; official submissions are closed.',
    );
    stdout.writeln('');
    stdout.writeln('Maintenance:');
    stdout.writeln(
      '  topiaforge updates index --repository owner/name --output path  Build a launcher update index.',
    );
    stdout.writeln(
      '  topiaforge release build-package ...   Build a release zip (CI maintainers).',
    );
    stdout.writeln(
      '  topiaforge release build-sdk-payload ... Build the extracted SDK/CLI acceptance payload.',
    );
    stdout.writeln(
      '  topiaforge release test-package ...    Smoke-test a release zip (CI maintainers).',
    );
    stdout.writeln(
      '  topiaforge release validate-policy ... Check catalog, pins, licensing, and provenance.',
    );
    stdout.writeln(
      '  topiaforge release validate-readiness ... Check exact-SHA human decision gates.',
    );
    stdout.writeln(
      '  topiaforge release build-metadata ...  Build deterministic BOM, SBOM, and checksums.',
    );
    stdout.writeln(
      '  topiaforge release build-update-metadata ... Sign immutable launcher update metadata.',
    );
    stdout.writeln(
      '  topiaforge release verify-update-metadata ... Verify update signature and archive inventory.',
    );
    stdout.writeln(
      '  topiaforge release build-platform-bundle ... Record a locally built, signed platform handoff.',
    );
    stdout.writeln(
      '  topiaforge release build-handoff ...      Assemble the policy-derived maintainer handoff.',
    );
    stdout.writeln(
      '  topiaforge release verify-handoff ...     Verify local release bytes; use '
      '--verify-embedded-ecosystem to recompute canonical payload identity.',
    );
    stdout.writeln('');
    stdout.writeln(
      'Run a command with wrong or missing arguments to see its full usage.',
    );
    stdout.writeln('Exit codes: 0 ok, 1 failure, 2 usage error.');
    stdout.writeln(
      'Docs: docs/YourFirstMod.md (walkthrough), docs/Modding.md (reference), '
      'docs/PublishingYourMod.md (publish).',
    );
  }
}
