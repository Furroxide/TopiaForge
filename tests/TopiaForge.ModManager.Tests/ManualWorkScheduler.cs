using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// A scheduler that holds work until a test runs it, standing in for a worker thread that has not got to it yet. The
    /// ModManager and multiplayer harnesses both compile it.
    /// </summary>
    internal sealed class ManualWorkScheduler : IMultiplayerWorkScheduler
    {
        private readonly Queue<Action> pending = new Queue<Action>();

        /// <summary>Gets how many scheduled pieces of work have not run.</summary>
        internal int Pending => pending.Count;

        /// <summary>Gets how many pieces of work were ever scheduled.</summary>
        internal int Scheduled { get; private set; }

        public void Schedule(Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            pending.Enqueue(work);
            Scheduled++;
        }

        /// <summary>Runs the work scheduled so far, and any it schedules in turn; returns how many ran.</summary>
        internal int RunAll()
        {
            var ran = 0;
            while (pending.Count > 0)
            {
                MultiplayerWorkSchedulers.RunGuarded(pending.Dequeue());
                ran++;
            }

            return ran;
        }
    }
}
