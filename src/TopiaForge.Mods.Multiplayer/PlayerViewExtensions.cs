using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods
{
    /// <summary>Captures an audience now; its membership stays on the host.</summary>
    [RequiresTopiaForge("0.2.0")]
    public static class PlayerViewExtensions
    {
        /// <summary>Addresses the listed players. A failed change releases all leases already taken by that call.</summary>
        [RequiresTopiaForge("0.2.0")]
        public static IViewControls View(this IEnumerable<IPlayer> players)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            var audience = new List<IPlayer>();
            var count = 0;
            foreach (var player in players)
            {
                if (player == null) throw new ArgumentException("An audience cannot contain null players.", nameof(players));
                if (!audience.Contains(player)) audience.Add(player);
                if (++count > 256) throw new ArgumentException("An audience supports at most 256 players.", nameof(players));
            }
            return new Controls(audience.ToArray());
        }
        private sealed class Controls : IViewControls
        {
            private readonly IPlayer[] players;
            internal Controls(IPlayer[] players) { this.players = players; }
            public OperationResult<IViewLease> Environment(string preset) => Change(v => v.Environment(preset));
            public OperationResult<IViewLease> Fog(VisionFog fog) => Change(v => v.Fog(fog));
            public OperationResult<IViewLease> Hide(IEntity entity) => Change(v => v.Hide(entity));
            private OperationResult<IViewLease> Change(Func<IViewControls, OperationResult<IViewLease>> change)
            {
                var leases = new List<IViewLease>();
                try
                {
                    foreach (var player in players)
                    {
                        var result = change(player.View);
                        if (!result.TryGetValue(out var lease))
                        { Release(leases); return OperationResult<IViewLease>.Failure(result.ErrorCode, result.ErrorMessage); }
                        leases.Add(lease);
                    }
                    return OperationResult<IViewLease>.Success(new GroupLease(leases));
                }
                catch { Release(leases); throw; }
            }
        }
        private static void Release(List<IViewLease> leases)
        {
            var failures = new List<Exception>();
            foreach (var lease in leases) try { lease.Dispose(); } catch (Exception error) { failures.Add(error); }
            leases.Clear();
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private sealed class GroupLease : IViewLease
        {
            private readonly List<IViewLease> leases;
            internal GroupLease(List<IViewLease> leases) { this.leases = leases; }
            public bool IsActive => leases.Any(lease => lease.IsActive);
            public void Dispose() => Release(leases);
        }
    }
}
