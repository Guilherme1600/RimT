using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Verse;

namespace RimT
{
    /// <summary>
    /// A queue of lambdas that background threads post results into.
    /// Every game tick, Flush() is called on the main thread to apply them.
    ///
    /// RULE: background threads NEVER write game state directly.
    ///       They only enqueue an Action here, which runs on the main thread.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static ConcurrentQueue<Action> _queue;
        private static volatile bool _initialized;

        public static void Initialize()
        {
            _queue = new ConcurrentQueue<Action>();
            _initialized = true;
        }

        /// <summary>Post an action to be run on the main thread next flush.</summary>
        public static void Post(Action action)
        {
            if (!_initialized || action == null) return;
            _queue.Enqueue(action);
        }

        /// <summary>
        /// Execute all pending actions. Called once per game tick from the
        /// Harmony patch on Game.UpdatePlay().
        /// Hard-caps to 2000 actions per flush to avoid stutter on huge queues.
        /// </summary>
        public static void Flush()
        {
            if (!_initialized) return;
            int budget = 2000;
            while (budget-- > 0 && _queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex)
                {
                    if (RimTMod.Settings?.DebugLog == true)
                        Log.Warning($"[RimT] Dispatcher exception: {ex.Message}");
                }
            }
        }
    }
}
