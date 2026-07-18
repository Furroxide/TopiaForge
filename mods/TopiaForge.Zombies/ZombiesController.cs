using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Safe-SDK wave-survival session using opaque RobotKit entities.</summary>
    internal sealed class ZombiesController : IDisposable
    {
        private sealed class Enemy
        {
            public Enemy(IRobotAgent agent, ZombieArchetype archetype, IDisposable cleanup)
            {
                Agent = agent;
                Archetype = archetype;
                Cleanup = cleanup;
                Health = archetype.Health;
            }

            public IRobotAgent Agent { get; }
            public ZombieArchetype Archetype { get; }
            public IDisposable Cleanup { get; }
            public float Health { get; set; }
            public float AttackCooldown { get; set; }
        }

        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly IRobotAgentService robots;
        private readonly Action endSession;
        private readonly ZombieRoster roster;
        private readonly Random random = new Random(1949);
        private readonly List<Enemy> enemies = new List<Enemy>();
        private readonly IDisposable updateSubscription;
        private readonly IInputAction? fireAction;
        private readonly IInputAction? broadcastAction;
        private IUiSurface? hud;
        private ITimeControlService? time;
        private ITimeLease? superhotDriver;
        private ITimeLease? playerExemption;
        private ITimeLease? gameOverFreeze;
        private IPlayerControlLease? gameOverControl;
        private string hudText = string.Empty;
        private float integrity;
        private float maximumIntegrity;
        private float spawnTimer;
        private float waveTimer;
        private float fireCooldown;
        private float standDownTimer;
        private int wave;
        private int pendingSpawns;
        private int spawnSerial;
        private int score;
        private bool gameOver;
        private bool disposed;

        public ZombiesController(
            IModContext context,
            ZombiesConfig config,
            IRobotAgentService robots,
            Action endSession)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.robots = robots ?? throw new ArgumentNullException(nameof(robots));
            this.endSession = endSession ?? throw new ArgumentNullException(nameof(endSession));
            roster = new ZombieRoster(config);
            maximumIntegrity = config.PlayerIntegrity;
            integrity = maximumIntegrity;
            waveTimer = config.StartingCountdownSeconds;

            fireAction = RegisterAction(new InputActionDefinition(
                "zapper-fire",
                "Fire zapper",
                new[] { InputBinding.MouseButton(InputMouseButton.Primary) }));
            broadcastAction = RegisterAction(new InputActionDefinition(
                "stand-down-broadcast",
                "Broadcast stand-down",
                new[] { InputBinding.Key(config.BroadcastKey) }));
            updateSubscription = context.Events.SubscribeUpdate(Update);

            var hudResult = context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-status",
                "ZOMBIES // SURVIVAL",
                string.Empty,
                UiSurfaceKind.Hud,
                400f,
                180f));
            if (hudResult.TryGetValue(out var surface))
            {
                hud = surface;
                hud.Show();
            }

            if (config.SuperhotMode
                && context.Extensions.TryGet<ITimeControlService>(out var timeService)
                && timeService != null
                && timeService.IsAvailable)
            {
                time = timeService;
                timeService.SetDriver("zombies-superhot", new SuperhotTimeDriver())
                    .TryGetValue(out superhotDriver);
                timeService.ExemptPlayer("zombies-superhot-player")
                    .TryGetValue(out playerExemption);
            }

            RefreshHud(force: true);
        }

        private IInputAction? RegisterAction(InputActionDefinition definition)
        {
            var result = context.Input.RegisterAction(definition);
            if (result.TryGetValue(out var action))
            {
                return action;
            }

            context.Logger.Warn(
                "Zombies input '" + definition.Name + "' is unavailable (" +
                result.ErrorCode + "): " + result.ErrorMessage);
            return null;
        }

        public OperationResult<string> Restart()
        {
            if (disposed)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "The Zombies session is not active.");
            }

            ClearEnemies();
            gameOverFreeze?.Dispose();
            gameOverFreeze = null;
            gameOverControl?.Dispose();
            gameOverControl = null;
            maximumIntegrity = config.PlayerIntegrity;
            integrity = maximumIntegrity;
            spawnTimer = 0f;
            waveTimer = config.StartingCountdownSeconds;
            fireCooldown = 0f;
            standDownTimer = 0f;
            wave = 0;
            pendingSpawns = 0;
            score = 0;
            gameOver = false;
            RefreshHud(force: true);
            context.Ui.ShowToast("Zombies run restarted.", UiTone.Success);
            return OperationResult<string>.Success("Zombies run restarted.");
        }

        public OperationResult<string> BroadcastStandDown()
        {
            if (disposed || gameOver)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "The horde is not active.");
            }

            standDownTimer = config.StandDownSeconds;
            foreach (var enemy in enemies)
            {
                if (enemy.Agent.IsAlive)
                {
                    enemy.Agent.Stop();
                    enemy.Agent.SetEmote(":warning:");
                }
            }

            context.Ui.ShowToast("Stand-down broadcast sent.", UiTone.Warning);
            return OperationResult<string>.Success("The horde is standing down temporarily.");
        }

        public string DescribeStatus()
        {
            RemoveDeadEnemies();
            return "wave=" + wave.ToString(CultureInfo.InvariantCulture)
                + ", alive=" + enemies.Count.ToString(CultureInfo.InvariantCulture)
                + ", integrity=" + integrity.ToString("0", CultureInfo.InvariantCulture)
                + ", score=" + score.ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            updateSubscription.Dispose();
            fireAction?.Dispose();
            broadcastAction?.Dispose();
            gameOverFreeze?.Dispose();
            gameOverControl?.Dispose();
            superhotDriver?.Dispose();
            playerExemption?.Dispose();
            hud?.Dispose();
            hud = null;
            ClearEnemies();
        }

        private void Update(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            fireCooldown = Math.Max(0f, fireCooldown - deltaTime);
            standDownTimer = Math.Max(0f, standDownTimer - deltaTime);

            if (fireAction?.WasPressed == true)
            {
                FireZapper();
            }

            if (broadcastAction?.WasPressed == true)
            {
                BroadcastStandDown();
            }

            if (gameOver)
            {
                RefreshHud(force: false);
                return;
            }

            RemoveDeadEnemies();
            AdvanceWaves(deltaTime);
            AdvanceEnemies(deltaTime);
            RefreshHud(force: false);
        }

        private void AdvanceWaves(float deltaTime)
        {
            if (pendingSpawns > 0)
            {
                spawnTimer -= deltaTime;
                if (spawnTimer <= 0f && enemies.Count < config.MaxAliveZombies)
                {
                    if (TrySpawnEnemy())
                    {
                        pendingSpawns--;
                    }

                    spawnTimer = config.SpawnIntervalSeconds;
                }

                return;
            }

            if (enemies.Count > 0)
            {
                return;
            }

            waveTimer -= deltaTime;
            if (waveTimer <= 0f)
            {
                wave++;
                pendingSpawns = Math.Min(
                    config.MaxAliveZombies,
                    config.BaseZombiesPerWave + ((wave - 1) * config.ZombiesPerWaveIncrement));
                spawnTimer = 0f;
                waveTimer = config.InterWaveDelaySeconds;
                context.Ui.ShowToast("Wave " + wave.ToString(CultureInfo.InvariantCulture), UiTone.Warning);
            }
        }

        private bool TrySpawnEnemy()
        {
            if (!robots.IsAvailable || !context.Player.TryGetSnapshot(out var player) || player == null)
            {
                return false;
            }

            var kind = config.ArchetypesEnabled ? roster.PickKind(wave, random) : ZombieKind.Grunt;
            var archetype = roster.Get(kind);
            var angle = spawnSerial++ * 2.399963f;
            var radialRange = Math.Max(0f, config.SpawnRadius - config.MinSpawnDistance);
            var radius = config.MinSpawnDistance + ((spawnSerial % 7) / 6f * radialRange);
            var position = player.Position + new Vec3(
                (float)Math.Cos(angle) * radius,
                config.SpawnHeightOffset,
                (float)Math.Sin(angle) * radius);

            var groundOrigin = position + new Vec3(0f, 4f, 0f);
            if (context.Physics.TryRaycast(
                new Ray(groundOrigin, new Vec3(0f, -1f, 0f)),
                16f,
                out var ground) && ground != null)
            {
                position = ground.Point + new Vec3(0f, config.SpawnHeightOffset, 0f);
            }

            var request = new RobotAgentSpawnRequest(
                position,
                brainMode: RobotBrainMode.Dormant,
                gait: archetype.Gait,
                moveSpeed: archetype.MoveSpeed,
                turnSpeed: config.ZombieTurnSpeed,
                stopDistance: archetype.StopDistance,
                tint: archetype.Tint,
                name: "Infected " + archetype.DisplayName,
                scale: archetype.Scale,
                interaction: RobotInteractionOptions.DisableNativeTalk());

            var spawnResult = robots.Spawn(request);
            if (!spawnResult.TryGetValue(out var agent) || agent == null)
            {
                return false;
            }

            if (config.EnableEnemyEmotes)
            {
                agent.SetEmote(archetype.Emote);
            }

            agent.MoveTo(player.Position);

            var cleanup = context.Lifetime.Defer(agent.Dispose);
            enemies.Add(new Enemy(agent, archetype, cleanup));
            return true;
        }

        private void AdvanceEnemies(float deltaTime)
        {
            if (!context.Player.TryGetSnapshot(out var player) || player == null)
            {
                return;
            }

            foreach (var enemy in enemies)
            {
                if (!enemy.Agent.IsAlive)
                {
                    continue;
                }

                enemy.AttackCooldown = Math.Max(0f, enemy.AttackCooldown - deltaTime);
                if (standDownTimer > 0f)
                {
                    continue;
                }

                enemy.Agent.MoveTo(player.Position);

                if (enemy.AttackCooldown > 0f
                    || Vec3.Distance(enemy.Agent.Position, player.Position) > config.ZombieAttackRange)
                {
                    continue;
                }

                enemy.AttackCooldown = enemy.Archetype.AttackCooldown;
                integrity = Math.Max(0f, integrity - enemy.Archetype.AttackDamage);
                context.Player.Damage(new PlayerDamageRequest(
                    enemy.Archetype.AttackDamage,
                    "infected robot"));
                if (integrity <= 0f)
                {
                    EnterGameOver();
                    return;
                }
            }
        }

        private void FireZapper()
        {
            if (gameOver || fireCooldown > 0f)
            {
                return;
            }

            fireCooldown = config.ZapperCooldownSeconds;
            if (!context.Player.TryGetSnapshot(out var player) || player == null)
            {
                context.Ui.ShowToast("Zapper camera unavailable.", UiTone.Warning);
                return;
            }

            context.Audio.Play(new AudioPlayRequest("zombies.zapper", 0.75f, false, player.Position));
            if (!context.Physics.TryRaycast(player.AimRay, config.ZapperRange, out var hit) || hit == null)
            {
                return;
            }

            var enemy = FindEnemy(hit.Entity.Id);
            if (enemy == null)
            {
                return;
            }

            enemy.Health -= config.ZapperDamage;
            enemy.Agent.ApplyDamage(config.ZapperDamage, RobotDamageType.Electricity, "zapper");
            if (config.ZapperImpactForce > 0f)
            {
                enemy.Agent.Knockback(player.AimRay.Direction * config.ZapperImpactForce);
            }

            if (enemy.Health > 0f)
            {
                return;
            }

            enemy.Agent.Kill(RobotDamageType.Electricity, "zapper");
            score += enemy.Archetype.Score;
            context.Ui.ShowToast(
                enemy.Archetype.DisplayName + " neutralized  +"
                + enemy.Archetype.Score.ToString(CultureInfo.InvariantCulture),
                UiTone.Success);
            RemoveDeadEnemies();
        }

        private Enemy? FindEnemy(string entityId)
        {
            for (var index = 0; index < enemies.Count; index++)
            {
                if (string.Equals(enemies[index].Agent.Id, entityId, StringComparison.Ordinal))
                {
                    return enemies[index];
                }
            }

            return null;
        }

        private void RemoveDeadEnemies()
        {
            var changed = false;
            for (var index = enemies.Count - 1; index >= 0; index--)
            {
                if (enemies[index].Agent.IsAlive && enemies[index].Health > 0f)
                {
                    continue;
                }

                enemies[index].Cleanup.Dispose();
                enemies.RemoveAt(index);
                changed = true;
            }

            if (changed)
            {
                RefreshHud(force: true);
            }
        }

        private void EnterGameOver()
        {
            if (gameOver)
            {
                return;
            }

            gameOver = true;
            foreach (var enemy in enemies)
            {
                enemy.Agent.Stop();
            }

            if (time == null)
            {
                if (context.Extensions.TryGet<ITimeControlService>(out var timeService)
                    && timeService != null)
                {
                    time = timeService;
                }
            }

            if (time?.IsAvailable == true)
            {
                time.Freeze("zombies-game-over", suspendPlayer: true)
                    .TryGetValue(out gameOverFreeze);
            }
            else
            {
                var control = context.Player.AcquireControl("Zombies game over");
                if (control.TryGetValue(out var lease))
                {
                    gameOverControl = lease;
                }
            }

            var result = context.Ui.ShowModal(
                new UiModalRequest(
                    "SYSTEM FAILURE",
                    "Score " + score.ToString(CultureInfo.InvariantCulture)
                    + " // Wave " + wave.ToString(CultureInfo.InvariantCulture)
                    + "\n\nRestart this run?",
                    "RESTART",
                    "RETURN TO MENU",
                    destructive: true),
                confirmed =>
                {
                    if (confirmed)
                    {
                        Restart();
                    }
                    else
                    {
                        endSession();
                    }
                });
            if (!result.Succeeded)
            {
                context.Logger.Warn("Zombies game-over modal unavailable: " + result.ErrorMessage);
            }

            RefreshHud(force: true);
        }

        private void ClearEnemies()
        {
            for (var index = enemies.Count - 1; index >= 0; index--)
            {
                enemies[index].Cleanup.Dispose();
            }

            enemies.Clear();
        }

        private void RefreshHud(bool force)
        {
            if (hud == null)
            {
                return;
            }

            var next = "WAVE  " + wave.ToString(CultureInfo.InvariantCulture)
                + "    HOSTILES  " + enemies.Count.ToString(CultureInfo.InvariantCulture)
                + "\nINTEGRITY  " + integrity.ToString("0", CultureInfo.InvariantCulture)
                + " / " + maximumIntegrity.ToString("0", CultureInfo.InvariantCulture)
                + "\nSCORE  " + score.ToString(CultureInfo.InvariantCulture)
                + "    " + config.BroadcastKey + " STAND DOWN"
                + (gameOver ? "\nSYSTEM FAILURE" : string.Empty);
            if (force || !string.Equals(next, hudText, StringComparison.Ordinal))
            {
                hudText = next;
                hud.SetBody(next);
            }
        }
    }
}
