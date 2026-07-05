using System.Collections.Generic;
using Robotopia.Mods.UnityUi;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// The spawn menu's ROBOTS tab: a live roster of every spawned robot — name, current program, objective
    /// state — with per-robot actions (PROGRAM opens the chat remotely, no walk-up needed; FOLLOW ME / IDLE
    /// program instantly with no LLM involved). Rows are pooled and rebound on a slow tick; all state and
    /// policy live on the controller (window discipline: render + forward clicks only).
    /// </summary>
    internal sealed class RobotRosterTab
    {
        private const float RefreshIntervalSeconds = 0.25f; // 4 Hz — badges feel live with zero per-frame churn

        private readonly SandboxController controller;
        private readonly QwLabel empty;
        private readonly QwContainer listHost;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<SpawnRegistry.SpawnedEntry> robots = new List<SpawnRegistry.SpawnedEntry>();
        private float nextRefreshAt;

        public RobotRosterTab(QwContainer pane, SandboxController controller)
        {
            this.controller = controller;
            pane.Label("Every robot on stage, what it is doing, and one-click programs.", QwTextStyle.Caption)
                .Tone(QwTone.Muted);
            empty = pane.Label("No robots yet — spawn one on the NPCS tab.", QwTextStyle.Body);
            empty.Tone(QwTone.Muted);
            listHost = pane.Scroll(QwGap.Sm, QwGap.Xs).Content.Column(QwGap.Sm);
        }

        /// <summary>Slow-tick rebind; the window calls this only while the ROBOTS tab is visible.</summary>
        public void Update()
        {
            if (UnityEngine.Time.unscaledTime < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = UnityEngine.Time.unscaledTime + RefreshIntervalSeconds;
            controller.CollectRobots(robots);
            empty.SetVisible(robots.Count == 0);

            for (var index = 0; index < robots.Count; index++)
            {
                var row = index < rows.Count ? rows[index] : CreateRow();
                Bind(row, robots[index]);
            }

            // Rows past the live set leave the layout entirely (SetActive, like the prop cards).
            for (var index = robots.Count; index < rows.Count; index++)
            {
                rows[index].Bound = null;
                rows[index].Root.Go.SetActive(false);
            }
        }

        private Row CreateRow()
        {
            var row = new Row();
            row.Root = listHost.Column(QwGap.Xs, QwGap.Xs);

            var info = row.Root.Row(QwGap.Sm);
            row.Name = info.Label(string.Empty, QwTextStyle.Body).NoWrap();
            row.Program = info.Badge(string.Empty);
            row.State = info.Badge(string.Empty);

            // Handlers bind once against the row's live entry — pooled rows rebind entries, never handlers.
            var actions = row.Root.Row(QwGap.Sm);
            row.ProgramButton = actions.Button("PROGRAM", () =>
            {
                if (row.Bound is { } entry)
                {
                    controller.ProgramRobotFromRoster(entry);
                }
            });
            row.FollowButton = actions.Button("FOLLOW ME", () =>
            {
                if (row.Bound is { } entry)
                {
                    controller.FollowMeFromRoster(entry);
                }
            }, QwButtonStyle.Outline);
            row.IdleButton = actions.Button("IDLE", () =>
            {
                if (row.Bound is { } entry)
                {
                    controller.IdleFromRoster(entry);
                }
            }, QwButtonStyle.Outline);
            row.Root.Divider();

            rows.Add(row);
            return row;
        }

        private void Bind(Row row, SpawnRegistry.SpawnedEntry entry)
        {
            row.Bound = entry;
            row.Root.Go.SetActive(true);
            row.Name.SetText(entry.DisplayName);

            var robot = entry.Robot!;
            var program = controller.RobotProgramBadge(robot);
            var tone = program == "NONE" ? QwTone.Neutral : program == "AUTONOMOUS" ? QwTone.Accent : QwTone.Success;
            row.Program.Set(program, tone);

            var state = controller.RobotStateBadge(robot);
            row.State.Set(state, QwTone.Neutral);
            row.State.SetVisible(state.Length > 0);

            row.ProgramButton.SetEnabled(controller.ProgrammingAvailable);
            var quick = controller.ObjectivesAvailable;
            row.FollowButton.SetEnabled(quick);
            row.IdleButton.SetEnabled(quick);
        }

        private sealed class Row
        {
            public QwContainer Root = null!;
            public QwLabel Name = null!;
            public QwBadge Program = null!;
            public QwBadge State = null!;
            public QwButton ProgramButton = null!;
            public QwButton FollowButton = null!;
            public QwButton IdleButton = null!;
            public SpawnRegistry.SpawnedEntry? Bound;
        }
    }
}
