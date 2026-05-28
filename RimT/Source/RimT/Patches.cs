using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
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
    //  PATCH 1 – Game.UpdatePlay
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
    //  PATCH 2 – TickList.Tick  ← O LOOP PRINCIPAL
    //  Interceta o tick de cada Thing antes de correr.
    //  Para pawns non-colono: distribui em buckets de 2 ticks.
    //  Colonos: sempre correm.
    //  Buildings/outros Things: sempre correm (não tocamos).
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TickList), "Tick")]
    internal static class Patch_TickList_Tick
    {
        // Substituir o Tick inteiro é arriscado — em vez disso
        // vamos usar o Pawn.TickInterval que já sabemos existir
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 3 – Pawn.TickInterval  (existe na 1.6, confirmado no stack trace)
    //  Throttle do tick completo do pawn para non-colonos.
    //  Distribui em 2 buckets — cada non-colono corre em ticks alternados.
    //  Colonos: sempre correm.
    //  SAFE: não bloqueia rendering, só o tick de lógica.
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn), "TickInterval")]
    internal static class Patch_Pawn_TickInterval
    {
        [HarmonyPrefix]
        internal static bool Prefix(Pawn __instance, int delta)
        {
            try
            {
                // Colonos, prisioneiros, pawns do jogador: sempre correm
                if (__instance.IsColonist) return true;
                if (__instance.IsPrisonerOfColony) return true;
                if (__instance.Faction == Faction.OfPlayer) return true;
                if (__instance.Dead || !__instance.Spawned) return true;

                // Pawns em mental state, em combate, ou com saude critica: sempre correm
                if (__instance.InMentalState) return true;
                if (__instance.health?.summaryHealth?.SummaryHealthPercent < 0.3f) return true;

                // Pawns hostis que estao a atacar colonos: sempre correm
                if (__instance.HostileTo(Faction.OfPlayer) && __instance.CurJob?.def == JobDefOf.AttackMelee)
                    return true;

                // Todos os outros non-colonos: bucket de 2 ticks
                // Metade corre nos ticks pares, metade nos impares
                return Find.TickManager.TicksGame % 2 == __instance.thingIDNumber % 2;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 4 – JobGiver_Work.TryIssueJobPackage
    //  - Pawn com job activo: salta
    //  - Colonos sem job: stagger de 3 ticks
    //  - Animais/inimigos: cada 60/30 ticks
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

                // Colonos: stagger de 3 ticks
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
    //  PATCH 5 – Pawn_MindState.MindStateTickInterval (confirmado no log)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Pawn_MindState), "MindStateTickInterval")]
    internal static class Patch_MindState_TickInterval
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
                return Find.TickManager.TicksGame % 5 == pawn.thingIDNumber % 5;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 6 – MentalBreaker.MentalBreakerTickInterval (confirmado no log)
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_MentalBreaker_TickInterval
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
    //  PATCH 7 – NeedsTracker
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
            return MethodFinder.Find(type,
                new[] { "NeedsTrackerTick", "TickInterval", "Tick", "NeedsTick", "NeedsTrackerTickInterval" },
                "NeedsTracker");
        }

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_NeedsTracker __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.Dead || !pawn.Spawned) return true;
                if (pawn.IsColonist) return true;
                return Find.TickManager.TicksGame % 150 == pawn.thingIDNumber % 150;
            }
            catch { return true; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PATCH 8 – RelationsTracker
    // ══════════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    internal static class Patch_RelationsTracker_Tick
    {
        private static FieldInfo _pawnField;

        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod()
        {
            var type = typeof(Pawn_RelationsTracker);
            _pawnField = AccessTools.Field(type, "pawn");
            return MethodFinder.Find(type,
                new[] { "RelationsTrackerTick", "TickInterval", "Tick", "RelationsTick", "RelationsTrackerTickInterval" },
                "RelationsTracker");
        }

        [HarmonyPrefix]
        internal static bool Prefix(object __instance)
        {
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.IsColonist) return true;
                return Find.TickManager.TicksGame % 10 == pawn.thingIDNumber % 10;
            }
            catch { return true; }
        }
    }
}
