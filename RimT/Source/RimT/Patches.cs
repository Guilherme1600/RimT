using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimT
{
    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 1 – Game.UpdatePlay → flush dispatcher + inject GameComponent
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
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 2 – Pathfinder: cache path costs per region pair
    //  PathFinder.FindPath is the #1 CPU hog with many pawns.
    //  We cache "no path found" results to avoid re-running expensive searches.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PathFinder), "FindPath",
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode), typeof(PathFinderCostTuning) })]
    internal static class Patch_PathFinder_FindPath
    {
        // Key: (startRegionID, destRegionID, traverseMode) → tick when "no path" was found
        private static readonly Dictionary<long, int> _noPathCache = new Dictionary<long, int>(1024);
        private const int CACHE_DURATION = 180; // 3 seconds

        private static long MakeKey(IntVec3 start, LocalTargetInfo dest, TraverseParms tp)
        {
            // Simple hash combining start cell, dest cell, traversal mode
            unchecked
            {
                long h = start.x * 1000003L + start.z;
                h = h * 1000003L + dest.Cell.x;
                h = h * 1000003L + dest.Cell.z;
                h = h * 31L + (int)tp.mode;
                return h;
            }
        }

        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 start, LocalTargetInfo dest, TraverseParms traverseParms,
            ref PawnPath __result)
        {
            try
            {
                int now = Find.TickManager.TicksGame;
                long key = MakeKey(start, dest, traverseParms);
                if (_noPathCache.TryGetValue(key, out int cachedTick) && now - cachedTick < CACHE_DURATION)
                {
                    __result = PawnPath.NotFound;
                    return false;
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(IntVec3 start, LocalTargetInfo dest, TraverseParms traverseParms,
            PawnPath __result)
        {
            try
            {
                if (__result == null || __result == PawnPath.NotFound)
                {
                    long key = MakeKey(start, dest, traverseParms);
                    _noPathCache[key] = Find.TickManager.TicksGame;
                }
                // Evict old entries periodically
                if (_noPathCache.Count > 4096)
                    _noPathCache.Clear();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 3 – Reachability cache: skip redundant IsReachable calls
    //  Reachability.CanReach is called thousands of times per tick.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Reachability), "CanReach",
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    internal static class Patch_Reachability_CanReach
    {
        private static readonly Dictionary<long, (bool result, int tick)> _cache
            = new Dictionary<long, (bool, int)>(2048);
        private const int CACHE_DURATION = 120;

        private static long MakeKey(IntVec3 start, LocalTargetInfo dest, TraverseParms tp)
        {
            unchecked
            {
                long h = start.x * 1000003L + start.z;
                h = h * 1000003L + dest.Cell.x;
                h = h * 1000003L + dest.Cell.z;
                h = h * 31L + (int)tp.mode;
                return h;
            }
        }

        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 start, LocalTargetInfo dest,
            PathEndMode peMode, TraverseParms traverseParams, ref bool __result)
        {
            try
            {
                long key = MakeKey(start, dest, traverseParams);
                int now = Find.TickManager.TicksGame;
                if (_cache.TryGetValue(key, out var cached) && now - cached.tick < CACHE_DURATION)
                {
                    __result = cached.result;
                    return false;
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(IntVec3 start, LocalTargetInfo dest,
            TraverseParms traverseParams, bool __result)
        {
            try
            {
                long key = MakeKey(start, dest, traverseParams);
                _cache[key] = (__result, Find.TickManager.TicksGame);
                if (_cache.Count > 8192) _cache.Clear();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 4 – Job scan throttle: ListerThings.ThingsMatching
    //  WorkGivers call ThingsMatching every tick to find work targets.
    //  Cache the result list per request type for a few ticks.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ListerThings), "ThingsMatching")]
    internal static class Patch_ListerThings_ThingsMatching
    {
        private static readonly Dictionary<int, (List<Thing> list, int tick)> _cache
            = new Dictionary<int, (List<Thing>, int)>(256);
        private const int CACHE_DURATION = 30; // half a second

        [HarmonyPrefix]
        internal static bool Prefix(ThingRequest req, ref List<Thing> __result)
        {
            try
            {
                if (!RimTMod.Settings.OffloadHauling) return true;
                int key = req.GetHashCode();
                int now = Find.TickManager.TicksGame;
                if (_cache.TryGetValue(key, out var cached) && now - cached.tick < CACHE_DURATION)
                {
                    __result = cached.list;
                    return false;
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(ThingRequest req, List<Thing> __result)
        {
            try
            {
                if (!RimTMod.Settings.OffloadHauling) return;
                int key = req.GetHashCode();
                _cache[key] = (__result, Find.TickManager.TicksGame);
                if (_cache.Count > 512) _cache.Clear();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 5 – Haul opportunity cache (item-level)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(WorkGiver_HaulGeneral), "JobOnThing")]
    internal static class Patch_HaulGeneral_JobOnThing
    {
        private static readonly Dictionary<int, int> _noHaulCache = new Dictionary<int, int>(512);
        private const int CACHE_TICKS = 300;

        [HarmonyPrefix]
        internal static bool Prefix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            if (!RimTMod.Settings.OffloadHauling || forced) return true;
            int now = Find.TickManager.TicksGame;
            if (_noHaulCache.TryGetValue(t.thingIDNumber, out int cachedTick)
                && now - cachedTick < CACHE_TICKS)
            {
                __result = null;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(Thing t, Job __result)
        {
            if (!RimTMod.Settings.OffloadHauling) return;
            if (__result == null)
                _noHaulCache[t.thingIDNumber] = Find.TickManager.TicksGame;
            else
                _noHaulCache.Remove(t.thingIDNumber);
            if (_noHaulCache.Count > 2048) _noHaulCache.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 6 – Stat cache: GetStatValue is called constantly
    //  Stats like MoveSpeed, WorkSpeed etc. recalculate every call.
    //  Cache them per pawn per stat for 60 ticks.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(StatExtension), "GetStatValue")]
    internal static class Patch_StatExtension_GetStatValue
    {
        private static readonly Dictionary<long, (float val, int tick)> _cache
            = new Dictionary<long, (float, int)>(4096);
        private const int CACHE_DURATION = 60;

        private static long MakeKey(Thing thing, StatDef stat)
        {
            unchecked { return (long)thing.thingIDNumber * 100000L + stat.index; }
        }

        [HarmonyPrefix]
        internal static bool Prefix(Thing thing, StatDef stat, bool applyPostProcess, ref float __result)
        {
            if (thing == null || stat == null || !applyPostProcess) return true;
            // Only cache for pawns (most expensive)
            if (!(thing is Pawn)) return true;
            try
            {
                long key = MakeKey(thing, stat);
                int now = Find.TickManager.TicksGame;
                if (_cache.TryGetValue(key, out var cached) && now - cached.tick < CACHE_DURATION)
                {
                    __result = cached.val;
                    return false;
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        internal static void Postfix(Thing thing, StatDef stat, bool applyPostProcess, float __result)
        {
            if (thing == null || stat == null || !applyPostProcess || !(thing is Pawn)) return;
            try
            {
                long key = MakeKey(thing, stat);
                _cache[key] = (__result, Find.TickManager.TicksGame);
                if (_cache.Count > 8192) _cache.Clear();
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 7 – ThinkTree throttle for non-player pawns
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
    internal static class Patch_JobTracker_DetermineNextJob
    {
        private static readonly Dictionary<int, int> _lastThinkTick = new Dictionary<int, int>(512);
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_JobTracker __instance, ref ThinkResult __result)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist || pawn.IsPrisonerOfColony) return true;
                if (pawn.Faction == Faction.OfPlayer) return true;

                int now      = Find.TickManager.TicksGame;
                int id       = pawn.thingIDNumber;
                int interval = pawn.RaceProps?.Animal == true ? 30 : 15;

                if (_lastThinkTick.TryGetValue(id, out int last) && now - last < interval)
                {
                    __result = ThinkResult.NoJob;
                    return false;
                }
                _lastThinkTick[id] = now;
                return true;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 8 – MapPawns sync debounce
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(MapPawns), "EnsureFactionsAndPawnsListsInSync")]
    internal static class Patch_MapPawns_Sync
    {
        private static int _lastSyncTick = -1;

        [HarmonyPrefix]
        internal static bool Prefix()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now == _lastSyncTick) return false;
            _lastSyncTick = now;
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 9 – NeedsTracker dynamic patch
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_NeedsTracker_Tick
    {
        private static FieldInfo _pawnField;

        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod()
        {
            var type = typeof(Pawn_NeedsTracker);
            _pawnField = AccessTools.Field(type, "pawn");
            foreach (var name in new[] { "NeedsTrackerTick", "Tick", "NeedsTick" })
            {
                var m = AccessTools.Method(type, name);
                if (m != null) return m;
            }
            return null;
        }

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_NeedsTracker __instance)
        {
            if (!RimTMod.Settings.OffloadNeeds) return true;
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.Dead || !pawn.Spawned) return true;
                if (Find.TickManager.TicksGame % 150 != pawn.thingIDNumber % 150) return true;

                var needs = __instance.AllNeeds;
                if (needs == null || needs.Count == 0) return true;
                var snapshot = needs.ToArray();

                ThreadCoordinator.Schedule(() =>
                {
                    var levels = new float[snapshot.Length];
                    for (int i = 0; i < snapshot.Length; i++)
                        levels[i] = snapshot[i].CurLevel;

                    MainThreadDispatcher.Post(() =>
                    {
                        if (pawn.Dead || !pawn.Spawned) return;
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            try { snapshot[i].CurLevel = levels[i]; }
                            catch { }
                        }
                    });
                });
                return false;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 10 – SkillsTracker throttle (dynamic)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_SkillsTracker_Tick
    {
        private static FieldInfo _pawnField;

        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("RimWorld.Pawn_SkillsTracker")
                 ?? AccessTools.TypeByName("Verse.Pawn_SkillsTracker");
            if (t == null) return null;
            _pawnField = AccessTools.Field(t, "pawn");
            return AccessTools.Method(t, "SkillsTick");
        }

        [HarmonyPrefix]
        internal static bool Prefix(object __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;
                return Find.TickManager.TicksGame % 8 == pawn.thingIDNumber % 8;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 11 – MentalStateHandler throttle (dynamic)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_MentalStateHandler_Tick
    {
        private static FieldInfo _pawnField;

        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("RimWorld.MentalStateHandler")
                 ?? AccessTools.TypeByName("Verse.MentalStateHandler");
            if (t == null) return null;
            _pawnField = AccessTools.Field(t, "pawn");
            return AccessTools.Method(t, "MentalStateHandlerTick");
        }

        [HarmonyPrefix]
        internal static bool Prefix(object __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;
                if (pawn.InMentalState) return true;
                return Find.TickManager.TicksGame % 5 == pawn.thingIDNumber % 5;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 12 – Apparel changed debounce
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_ApparelTracker), "Notify_ApparelChanged")]
    internal static class Patch_ApparelTracker_Changed
    {
        private static readonly Dictionary<int, int> _lastChangeTick = new Dictionary<int, int>(128);
        private const int DEBOUNCE = 5;
        private static readonly FieldInfo _pawnField =
            AccessTools.Field(typeof(Pawn_ApparelTracker), "pawn");

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_ApparelTracker __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null) return true;
                int now = Find.TickManager.TicksGame;
                int id  = pawn.thingIDNumber;
                if (_lastChangeTick.TryGetValue(id, out int last) && now - last < DEBOUNCE)
                    return false;
                _lastChangeTick[id] = now;
                return true;
            }
            catch { return true; }
        }
    }
}
