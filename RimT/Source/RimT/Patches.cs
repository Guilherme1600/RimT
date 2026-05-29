using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimT
{
    internal static class MethodFinder
    {
        internal static MethodInfo Find(Type type, string[] candidates, string label)
        {
            foreach (var name in candidates)
            {
                var m = AccessTools.Method(type, name);
                if (m != null) return m;
            }
            if (RimTMod.Settings?.DebugLog == true)
            {
                var all = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Log.Warning($"[RimT] {label} nao encontrado. Metodos: {string.Join(", ", Array.ConvertAll(all, x => x.Name))}");
            }
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 1 – Game.UpdatePlay
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Game), "UpdatePlay")]
    internal static class Patch_Game_UpdatePlay
    {
        [HarmonyPostfix]
        internal static void Postfix(Game __instance)
        {
            try
            {
                ThreadCoordinator.WaitForAll(50);
                MainThreadDispatcher.Flush();
                RimTMod.TryInjectGameComponent(__instance);
                AsyncPathfinder.Cleanup();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 2 – Adaptive Tick Rate
    // Reduz o numero de ticks calculados por segundo globalmente.
    // TickRateDivisor=1 → normal. =2 → metade. =20 → 1/20.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TickManager), "DoSingleTick")]
    internal static class Patch_TickManager_DoSingleTick
    {
        private static int _counter = 0;

        [HarmonyPrefix]
        internal static bool Prefix()
        {
            try
            {
                int div = RimTMod.Settings?.TickRateDivisor ?? 1;
                if (div <= 1) return true;
                _counter++;
                if (_counter >= div) { _counter = 0; return true; }
                return false;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 3 – NeedsTrackerTickInterval → 11.95%
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_NeedsTracker), "NeedsTrackerTickInterval")]
    internal static class Patch_NeedsTracker
    {
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_NeedsTracker), "pawn");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_NeedsTracker __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.Dead || !pawn.Spawned) return true;
                if (pawn.IsColonist) return true;
                return Find.TickManager.TicksGame % 3 == pawn.thingIDNumber % 3;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 4 – MindStateTickInterval → 8.52%
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_MindState), "MindStateTickInterval")]
    internal static class Patch_MindState
    {
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_MindState), "pawn");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_MindState __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;
                if (pawn.InMentalState) return true;
                return Find.TickManager.TicksGame % 3 == pawn.thingIDNumber % 3;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 5 – MentalBreakerTickInterval
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_MentalBreaker
    {
        private static FieldInfo _pawnField;

        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Verse.AI.MentalBreaker")
                    ?? AccessTools.TypeByName("RimWorld.MentalBreaker");
            if (type == null) return null;
            _pawnField = AccessTools.Field(type, "pawn");
            return MethodFinder.Find(type,
                new[] { "MentalBreakerTickInterval", "TickInterval", "Tick" },
                "MentalBreaker");
        }

        [HarmonyPrefix]
        internal static bool Prefix(object __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;
                return Find.TickManager.TicksGame % 5 == pawn.thingIDNumber % 5;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 6 – HealthTick → 1.70%
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_HealthTracker), "HealthTick")]
    internal static class Patch_HealthTracker
    {
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_HealthTracker __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.Dead) return true;
                if (pawn.IsColonist || pawn.IsPrisonerOfColony) return true;
                if (pawn.health?.summaryHealth?.SummaryHealthPercent < 0.5f) return true;
                return Find.TickManager.TicksGame % 3 == pawn.thingIDNumber % 3;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 7 – JobGiver_Work
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(JobGiver_Work), "TryIssueJobPackage")]
    internal static class Patch_JobGiver_Work
    {
        private static readonly Dictionary<int, int> _lastScan = new Dictionary<int, int>(512);

        [HarmonyPrefix]
        internal static bool Prefix(Pawn pawn, ref ThinkResult __result)
        {
            try
            {
                if (pawn == null) return true;
                int now = Find.TickManager.TicksGame;
                int id  = pawn.thingIDNumber;

                if (pawn.jobs?.curJob != null)
                { __result = ThinkResult.NoJob; return false; }

                if (pawn.RaceProps?.Animal == true)
                {
                    if (_lastScan.TryGetValue(id, out int la) && now - la < 60)
                    { __result = ThinkResult.NoJob; return false; }
                    _lastScan[id] = now; return true;
                }

                if (!pawn.IsColonist && !pawn.IsPrisonerOfColony && pawn.Faction != Faction.OfPlayer)
                {
                    if (_lastScan.TryGetValue(id, out int ln) && now - ln < 30)
                    { __result = ThinkResult.NoJob; return false; }
                    _lastScan[id] = now; return true;
                }

                if (now % 3 != id % 3)
                {
                    if (_lastScan.TryGetValue(id, out int lc) && now - lc <= 3)
                    { __result = ThinkResult.NoJob; return false; }
                }
                _lastScan[id] = now;
                if (_lastScan.Count > 2048) _lastScan.Clear();
                return true;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 8 – Reachability cache (memoria de pathfinding)
    // CanReach chamado milhares de vezes/tick. Cache de 120 ticks.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Reachability), "CanReach",
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    internal static class Patch_Reachability_Cache
    {
        private struct Entry { public bool result; public int tick; }
        private static readonly Dictionary<long, Entry> _cache = new Dictionary<long, Entry>(4096);
        private const int TTL = 120;

        private static long Key(IntVec3 s, LocalTargetInfo d, PathEndMode m, TraverseMode tm)
        {
            unchecked
            {
                long h = s.x * 73856093L ^ s.z * 19349663L;
                h ^= (long)d.Cell.x * 83492791L ^ (long)d.Cell.z * 15485863L;
                h ^= (long)m * 31L ^ (long)tm * 17L;
                return h;
            }
        }

        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 start, LocalTargetInfo dest,
            PathEndMode peMode, TraverseParms traverseParams, ref bool __result)
        {
            try
            {
                long key = Key(start, dest, peMode, traverseParams.mode);
                int now  = Find.TickManager.TicksGame;
                if (_cache.TryGetValue(key, out var e) && now - e.tick < TTL)
                { __result = e.result; return false; }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(IntVec3 start, LocalTargetInfo dest,
            PathEndMode peMode, TraverseParms traverseParams, bool __result)
        {
            try
            {
                long key = Key(start, dest, peMode, traverseParams.mode);
                _cache[key] = new Entry { result = __result, tick = Find.TickManager.TicksGame };
                if (_cache.Count > 8192) _cache.Clear();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 9 – Async PathFollower (non-colonos)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_PathFollower), "StartPath")]
    internal static class Patch_PathFollower_StartPath
    {
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
        private static readonly FieldInfo _pathField =
            AccessTools.Field(typeof(Pawn_PathFollower), "curPath");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_PathFollower __instance,
            LocalTargetInfo dest, PathEndMode peMode)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null) return true;
                if (pawn.IsColonist || pawn.IsPrisonerOfColony) return true;
                if (pawn.Faction == Faction.OfPlayer) return true;
                if (pawn.HostileTo(Faction.OfPlayer) && pawn.mindState?.enemyTarget != null) return true;

                var map = pawn.Map;
                if (map == null) return true;

                var tp   = TraverseParms.For(pawn);
                long key = AsyncPathfinder.MakeKey(pawn.Position, dest, tp, peMode);

                if (AsyncPathfinder.TryGetCachedPath(key, out var cached))
                {
                    if (cached != null && cached != PawnPath.NotFound)
                    {
                        var old = _pathField?.GetValue(__instance) as PawnPath;
                        try { if (old != null && old != PawnPath.NotFound) old.ReleaseToPool(); } catch { }
                        _pathField?.SetValue(__instance, cached);
                        return false;
                    }
                    return true;
                }

                if (AsyncPathfinder.IsInFlight(key)) return false;
                AsyncPathfinder.RequestAsync(key, map, pawn.Position, dest, tp, peMode);
                return false;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 10 – ColonistBar.MarkColonistsDirty
    // Intervalo configuravel via slider nas settings.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ColonistBar), "MarkColonistsDirty")]
    internal static class Patch_ColonistBar_Dirty
    {
        private static int _lastFrame = -1;

        [HarmonyPrefix]
        internal static bool Prefix()
        {
            try
            {
                int interval = RimTMod.Settings?.ColonistBarInterval ?? 5;
                int frame = Time.frameCount;
                if (frame - _lastFrame < interval) return false;
                _lastFrame = frame;
                return true;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH 11 – PawnRenderer: throttle non-colonos fora de vista
    // Intervalo configuravel via slider nas settings.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PawnRenderer), "RenderPawnAt")]
    internal static class Patch_PawnRenderer
    {
        private static readonly Dictionary<int, int> _lastFrame = new Dictionary<int, int>(512);
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(PawnRenderer), "pawn")
            ?? AccessTools.Field(typeof(PawnRenderer), "_pawn")
            ?? AccessTools.Field(typeof(PawnRenderer), "ownedPawn");

        [HarmonyPrefix]
        internal static bool Prefix(PawnRenderer __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;

                var map = pawn.Map;
                if (map == null) return true;
                // Se está dentro do viewport: render normal
                if (map.IsPlayerHome && pawn.DrawPos.InBounds(map)) return true;

                int interval = RimTMod.Settings?.PawnRenderInterval ?? 3;
                int frame = Time.frameCount;
                int id    = pawn.thingIDNumber;
                if (_lastFrame.TryGetValue(id, out int last) && frame - last < interval)
                    return false;
                _lastFrame[id] = frame;
                if (_lastFrame.Count > 1024) _lastFrame.Clear();
                return true;
            }
            catch { return true; }
        }
    }
}
