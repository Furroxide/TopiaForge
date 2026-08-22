part of '../screens.dart';

class DeveloperScreen extends StatelessWidget {
  const DeveloperScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final workspace = state.developerWorkspace;
    final project = workspace?.project;
    final busy = state.isBusy;
    final hasInstall = state.gameInstall != null;
    // When a Unity project is being managed, show its VPM Packages pane instead of the C#-mod lifecycle panes.
    final isUnity = state.managedProject?.isUnity == true;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ScreenHeader(
          title: 'Developer',
          subtitle:
              'Environment, project lifecycle, and references.',
          trailing: Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilledButton.icon(
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperWorkspaceRefreshed()),
                icon: const Icon(Icons.refresh),
                label: const Text('Refresh'),
              ),
              OutlinedButton.icon(
                onPressed: busy
                    ? null
                    : () =>
                          _add(context, const DeveloperSampleProjectCreated()),
                icon: const Icon(Icons.create_new_folder_outlined),
                label: const Text('New Sample'),
              ),
            ],
          ),
        ),
        Expanded(
          child: ListView(
            key: const Key('developer-scroll'),
            padding: const EdgeInsets.all(16),
            children: [
              _ProjectsPane(state: state),
              const SizedBox(height: 16),
              if (!isUnity)
                BorderedPane(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Project',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 10),
                      if (project == null) ...[
                        const Text(
                          'No topiaforge.project.json detected from the launcher working directory.',
                        ),
                        const SizedBox(height: 12),
                        Wrap(
                          spacing: 10,
                          runSpacing: 10,
                          children: [
                            FilledButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperSampleProjectCreated(),
                                    ),
                              icon: const Icon(Icons.add),
                              label: const Text('Create Sample Project'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _showNewModDialog(context),
                              icon: const Icon(Icons.note_add_outlined),
                              label: const Text('New mod…'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperWorkspaceRefreshed(),
                                    ),
                              icon: const Icon(Icons.search),
                              label: const Text('Scan'),
                            ),
                          ],
                        ),
                      ] else ...[
                        _keyValue('Name', project.name),
                        _keyValue('ID', project.id),
                        _keyValue('Root', workspace!.projectRoot),
                        _keyValue(
                          'Dependencies',
                          project.dependencies.length.toString(),
                        ),
                        _keyValue(
                          'Lock',
                          workspace.lock == null
                              ? 'Missing'
                              : '${workspace.lock!.packages.length} package(s)',
                        ),
                        _keyValue('Props', workspace.generatedPropsPath),
                        const SizedBox(height: 12),
                        Wrap(
                          spacing: 10,
                          runSpacing: 10,
                          children: [
                            FilledButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperProjectResolved(),
                                    ),
                              icon: const Icon(Icons.sync),
                              label: const Text('Resolve / Restore'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperProjectPacked(),
                                    ),
                              icon: const Icon(Icons.inventory_2_outlined),
                              label: const Text('Pack'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy || !hasInstall
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperProjectInstalledToGame(),
                                    ),
                              icon: const Icon(Icons.system_update_alt),
                              label: const Text('Install to game'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperProjectFolderOpened(),
                                    ),
                              icon: const Icon(Icons.folder_open),
                              label: const Text('Open folder'),
                            ),
                            OutlinedButton.icon(
                              onPressed: busy
                                  ? null
                                  : () => _add(
                                      context,
                                      const DeveloperDoctorRequested(),
                                    ),
                              icon: const Icon(
                                Icons.health_and_safety_outlined,
                              ),
                              label: const Text('Doctor'),
                            ),
                          ],
                        ),
                      ],
                    ],
                  ),
                ),
              if (!isUnity) const SizedBox(height: 16),
              _EnvironmentPane(state: state),
              const SizedBox(height: 16),
              if (isUnity) ...[
                _PackagesPane(state: state),
                const SizedBox(height: 16),
              ] else ...[
                _ReferencePane(workspace: workspace),
                const SizedBox(height: 16),
                _DoctorPane(report: state.developerDoctor),
                const SizedBox(height: 16),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

// VCC-style multi-project registry list. Cards for each tracked project (mod or Unity), with Manage (loads it
// into the panes below), Open-in-Unity, and Remove. New/Add-existing seed the registry.
class _ProjectsPane extends StatelessWidget {
  const _ProjectsPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final busy = state.isBusy;
    final projects = state.developerProjects;
    final managedPath = state.managedProject?.path;
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Projects',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              if (state.unityEditors.isNotEmpty)
                StatusPill(
                  label: '${state.unityEditors.length} Unity editor(s)',
                  tone: StatusTone.info,
                  icon: Icons.view_in_ar_outlined,
                ),
            ],
          ),
          const SizedBox(height: 10),
          if (projects.isEmpty)
            const Text(
              'No projects tracked yet. Create a mod project or add an existing project (mod or Unity).',
              style: TextStyle(
                color: TopiaForgePalette.mutedText,
                fontSize: 12,
              ),
            )
          else
            ...projects.map(
              (project) => _projectCard(
                context,
                project,
                busy: busy,
                managed:
                    managedPath != null && _samePath(project.path, managedPath),
              ),
            ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 10,
            runSpacing: 10,
            children: [
              FilledButton.icon(
                onPressed: busy ? null : () => _showNewModDialog(context),
                icon: const Icon(Icons.note_add_outlined),
                label: const Text('New mod…'),
              ),
              OutlinedButton.icon(
                onPressed: busy
                    ? null
                    : () => _showNewUnityProjectDialog(context),
                icon: const Icon(Icons.view_in_ar_outlined),
                label: const Text('New Unity world…'),
              ),
              OutlinedButton.icon(
                onPressed: busy
                    ? null
                    : () => _showAddExistingProjectDialog(context),
                icon: const Icon(Icons.create_new_folder_outlined),
                label: const Text('Add existing…'),
              ),
              OutlinedButton.icon(
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperProjectsRefreshed()),
                icon: const Icon(Icons.refresh),
                label: const Text('Refresh'),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _projectCard(
    BuildContext context,
    RegisteredProject project, {
    required bool busy,
    required bool managed,
  }) {
    final canOpenInUnity = project.kind == ProjectKind.unityWorld;
    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.all(10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    project.name,
                    style: Theme.of(context).textTheme.titleSmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                StatusPill(
                  label: _kindLabel(project.kind),
                  tone: managed ? StatusTone.good : StatusTone.neutral,
                  icon: project.isUnity
                      ? Icons.view_in_ar_outlined
                      : Icons.extension,
                ),
              ],
            ),
            const SizedBox(height: 2),
            Text(
              project.path,
              style: const TextStyle(
                color: TopiaForgePalette.mutedText,
                fontSize: 11,
              ),
              overflow: TextOverflow.ellipsis,
            ),
            if (project.unityVersion.isNotEmpty)
              Text(
                'Unity ${project.unityVersion}',
                style: const TextStyle(
                  color: TopiaForgePalette.mutedText,
                  fontSize: 11,
                ),
              ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                FilledButton.tonalIcon(
                  onPressed: busy
                      ? null
                      : () => _add(
                          context,
                          DeveloperProjectManaged(project.path),
                        ),
                  icon: const Icon(Icons.tune, size: 16),
                  label: Text(managed ? 'Managing' : 'Manage'),
                ),
                if (canOpenInUnity)
                  OutlinedButton.icon(
                    onPressed: busy
                        ? null
                        : () => _add(
                            context,
                            DeveloperProjectOpenedInUnity(project.path),
                          ),
                    icon: const Icon(Icons.open_in_new, size: 16),
                    label: const Text('Open in Unity'),
                  ),
                OutlinedButton.icon(
                  onPressed: busy
                      ? null
                      : () => _add(
                          context,
                          DeveloperProjectRemoved(project.path),
                        ),
                  icon: const Icon(Icons.link_off, size: 16),
                  label: const Text('Remove'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  // Lenient path equality for the "Managing" badge (normalizes separators + trailing slash, case-insensitive).
  bool _samePath(String a, String b) {
    String norm(String s) =>
        s.replaceAll('\\', '/').replaceAll(RegExp(r'/+$'), '').toLowerCase();
    return norm(a) == norm(b);
  }

  String _kindLabel(ProjectKind kind) {
    switch (kind) {
      case ProjectKind.modCSharp:
        return 'Mod';
      case ProjectKind.unityWorld:
        return 'Unity world';
      case ProjectKind.unityPackage:
        return 'Unity package';
      case ProjectKind.unknown:
        return 'Project';
    }
  }
}

class _ReferencePane extends StatelessWidget {
  const _ReferencePane({required this.workspace});

  final DeveloperWorkspace? workspace;

  @override
  Widget build(BuildContext context) {
    final packages = workspace?.lock?.packages ?? const <LockedPackage>[];
    final exported = [
      for (final package in packages)
        for (final assembly in package.apiAssemblies)
          '${package.id} ${package.version}: $assembly',
    ];
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('C# References', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 10),
          if (exported.isEmpty)
            const Text('No exported API assemblies are resolved.')
          else
            ...exported.map(
              (item) => ListTile(
                dense: true,
                leading: const Icon(Icons.api),
                title: Text(item, overflow: TextOverflow.ellipsis),
              ),
            ),
          if (workspace?.issues.isNotEmpty == true) ...[
            const Divider(),
            ...workspace!.issues.map(_issueTile),
          ],
        ],
      ),
    );
  }
}

class _DoctorPane extends StatelessWidget {
  const _DoctorPane({required this.report});

  final DeveloperDoctorReport? report;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Doctor', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 10),
          if (report == null)
            const Text(
              'Run Doctor to check the project: toolchain, references, and restore status.',
            )
          else ...[
            StatusPill(
              label: report!.ok ? 'Ready' : 'Needs attention',
              tone: report!.ok ? StatusTone.good : StatusTone.warning,
              icon: report!.ok ? Icons.check_circle : Icons.warning,
            ),
            const SizedBox(height: 10),
            ...report!.messages.map(
              (message) => Text(message, overflow: TextOverflow.ellipsis),
            ),
            if (report!.issues.isNotEmpty) ...[
              const Divider(),
              ...report!.issues.map(_issueTile),
            ],
          ],
        ],
      ),
    );
  }
}
