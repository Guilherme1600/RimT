using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimT
{
    /// <summary>
    /// Async ThinkTree scorer — mimics RimThreaded's partial think-tree offload.
    ///
    /// The ThinkTree has two phases:
    ///   1. SCORING: each ThinkNode calculates its priority (read-only, safe in threads)
    ///   2. ISSUING: the winner issues a Job (must be on main thread)
    ///
    /// We offload phase 1 to workers for non-colonist pawns.
    /// Results cached for 30 ticks — pawn reuses last scored result until next cycle.
    /// </summary>
    public static class AsyncThinkTree
    {
        private static readonly ConcurrentDictionary<int, (float score, int tick)> _scoreCache
            = new ConcurrentDictionary<int, (float, int)>(512, 512);

        private static readonly ConcurrentDictionary<int, bool> _inFlight
            = new ConcurrentDictionary<int, bool>();

        private const int CACHE_TICKS = 30;

        public static bool TryGetCachedScore(int pawnId, out float score)
        {
            if (_scoreCache.TryGetValue(pawnId, out var cached))
            {
                int age = (Find.TickManager?.TicksGame ?? 0) - cached.tick;
                if (age < CACHE_TICKS)
                {
                    score = cached.score;
                    return true;
                }
            }
            score = 0f;
            return false;
        }

        public static void ScheduleScore(Pawn pawn, ThinkNode rootNode)
        {
            if (_inFlight.ContainsKey(pawn.thingIDNumber)) return;
            _inFlight[pawn.thingIDNumber] = true;
            int id = pawn.thingIDNumber;
            int tick = Find.TickManager?.TicksGame ?? 0;

            ThreadCoordinator.Schedule(() =>
            {
                try
                {
                    // TryIssueJobPackage scoring is read-only for priority nodes
                    // We only call GetPriority, not TryIssueJobPackage
                    float score = 0f;
                    if (rootNode != null)
                    {
                        // Safe: GetPriority only reads pawn state, doesn't write
                        score = rootNode.GetPriority(pawn);
                    }
                    MainThreadDispatcher.Post(() =>
                    {
                        _scoreCache[id] = (score, tick);
                        _inFlight.TryRemove(id, out _);
                    });
                }
                catch
                {
                    _inFlight.TryRemove(id, out _);
                }
            });
        }

        public static void Cleanup()
        {
            if (_scoreCache.Count > 2048) _scoreCache.Clear();
        }
    }
}
