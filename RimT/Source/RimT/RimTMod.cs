using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimT
{
    /// <summary>
    /// RimT entry point. Boots Harmony patches and the worker thread pool.
    /// Also injects the GameComponent manually since GameComponentDef doesn't exist.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RimTMod
    {
        public static readonly string ID = "RimT";
        public static RimTSettings Settings { get; private set; }

        private static readonly Harmony Harmony = new Harmony("rimT.multithreaded");

        static RimTMod()
        {
            try
            {
                Settings = LoadedModManager.GetMod<RimTModDef>()?.GetSettings<RimTSettings>()
                           ?? new RimTSettings();

                ThreadCoordinator.Initialize(Settings.WorkerThreadCount);
                MainThreadDispatcher.Initialize();

                // Patch each class individually so one failure doesn't kill all patches
                SafePatch(typeof(Patch_Game_UpdatePlay));
                SafePatch(typeof(Patch_PathFinder_FindPath));
                SafePatch(typeof(Patch_Reachability_CanReach));
                SafePatch(typeof(Patch_ListerThings_ThingsMatching));
                SafePatch(typeof(Patch_HaulGeneral_JobOnThing));
                SafePatch(typeof(Patch_StatExtension_GetStatValue));
                SafePatch(typeof(Patch_JobTracker_DetermineNextJob));
                SafePatch(typeof(Patch_MapPawns_Sync));
                SafePatch(typeof(Patch_ApparelTracker_Changed));
                SafePatch(typeof(Patch_MentalStateHandler_Tick));
                SafePatch(typeof(Patch_SkillsTracker_Tick));
                SafePatch(typeof(Patch_NeedsTracker_Tick));

                Log.Message($"[RimT] Iniciado com {Settings.WorkerThreadCount} worker threads.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimT] Falha ao iniciar: {ex}");
            }
        }

        private static void SafePatch(Type patchClass)
        {
            try
            {
                var processor = new PatchClassProcessor(Harmony, patchClass);
                processor.Patch();
            }
            catch (Exception ex)
            {
                // Log warning but continue — other patches still work
                Log.Warning($"[RimT] Patch ignorado ({patchClass.Name}): {ex.Message}");
            }
        }

        /// <summary>Called by Patch_Game_UpdatePlay to inject GameComponent once the game is loaded.</summary>
        public static void TryInjectGameComponent(Game game)
        {
            if (game == null) return;
            if (game.components == null) return;
            // Only inject once
            if (game.components.Any(c => c is RimTPerformanceMonitor)) return;
            try
            {
                var monitor = new RimTPerformanceMonitor(game);
                game.components.Add(monitor);
                if (Settings?.DebugLog == true)
                    Log.Message("[RimT] PerformanceMonitor injetado.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimT] Não foi possível injetar PerformanceMonitor: {ex.Message}");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Mod class for settings window
    // ══════════════════════════════════════════════════════════════════════════
    public class RimTModDef : Mod
    {
        private RimTSettings _settings;

        public RimTModDef(ModContentPack content) : base(content)
        {
            _settings = GetSettings<RimTSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            _settings.DoWindowContents(inRect);
        }

        public override string SettingsCategory() => "RimT Performance";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Settings
    // ══════════════════════════════════════════════════════════════════════════
    public class RimTSettings : ModSettings
    {
        public int  WorkerThreadCount      = Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14);
        public bool OffloadNeeds           = true;
        public bool OffloadHealth          = true;
        public bool OffloadRelations       = true;
        public bool OffloadHauling         = true;
        public bool OffloadPathfindingWarm = true;
        public bool DebugLog               = false;

        public void DoWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.Label($"Worker threads: {WorkerThreadCount}  (CPUs lógicos: {Environment.ProcessorCount})");
            WorkerThreadCount = (int)list.Slider(WorkerThreadCount, 1, Math.Max(1, Environment.ProcessorCount - 1));
            list.CheckboxLabeled("Offload needs (fome/descanso/alegria)", ref OffloadNeeds);
            list.CheckboxLabeled("Offload health checks",                  ref OffloadHealth);
            list.CheckboxLabeled("Offload relationship recalc",            ref OffloadRelations);
            list.CheckboxLabeled("Offload hauling scan cache",             ref OffloadHauling);
            list.CheckboxLabeled("Warm pathfinding cache",                 ref OffloadPathfindingWarm);
            list.Gap();
            list.CheckboxLabeled("[Debug] Log verbose", ref DebugLog);
            list.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref WorkerThreadCount,      "workerThreadCount",
                Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14));
            Scribe_Values.Look(ref OffloadNeeds,           "offloadNeeds",           true);
            Scribe_Values.Look(ref OffloadHealth,          "offloadHealth",          true);
            Scribe_Values.Look(ref OffloadRelations,       "offloadRelations",       true);
            Scribe_Values.Look(ref OffloadHauling,         "offloadHauling",         true);
            Scribe_Values.Look(ref OffloadPathfindingWarm, "offloadPathfindingWarm", true);
            Scribe_Values.Look(ref DebugLog,               "debugLog",               false);
        }
    }
}
