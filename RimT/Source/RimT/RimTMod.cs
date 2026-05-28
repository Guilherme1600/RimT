using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimT
{
    [StaticConstructorOnStartup]
    public static class RimTMod
    {
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

                SafePatch(typeof(Patch_Game_UpdatePlay));
                SafePatch(typeof(Patch_Pawn_TickInterval));
                SafePatch(typeof(Patch_JobGiver_Work));
                SafePatch(typeof(Patch_MindState_TickInterval));
                SafePatch(typeof(Patch_MentalBreaker_TickInterval));
                SafePatch(typeof(Patch_NeedsTracker_Tick));
                SafePatch(typeof(Patch_RelationsTracker_Tick));

                Log.Message($"[RimT] Iniciado — {Settings.WorkerThreadCount} threads, {Environment.ProcessorCount} CPUs.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimT] Falha: {ex}");
            }
        }

        private static void SafePatch(Type patchClass)
        {
            try { new PatchClassProcessor(Harmony, patchClass).Patch(); }
            catch (Exception ex)
            {
                Log.Warning($"[RimT] Patch ignorado ({patchClass.Name}): {ex.Message}");
            }
        }

        public static void TryInjectGameComponent(Game game)
        {
            if (game?.components == null) return;
            if (game.components.Any(c => c is RimTPerformanceMonitor)) return;
            try { game.components.Add(new RimTPerformanceMonitor(game)); }
            catch { }
        }
    }

    public class RimTModDef : Mod
    {
        private RimTSettings _settings;
        public RimTModDef(ModContentPack content) : base(content)
            => _settings = GetSettings<RimTSettings>();
        public override void DoSettingsWindowContents(Rect inRect)
            => _settings.DoWindowContents(inRect);
        public override string SettingsCategory() => "RimT Performance";
    }

    public class RimTSettings : ModSettings
    {
        public int  WorkerThreadCount = Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14);
        public bool DebugLog          = false;

        public void DoWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.Label($"Worker threads: {WorkerThreadCount}   (CPUs: {Environment.ProcessorCount})");
            list.Label("Ideal: CPUs - 1. Testa e ve o que da mais TPS.");
            WorkerThreadCount = (int)list.Slider(WorkerThreadCount, 1, Math.Max(1, Environment.ProcessorCount - 1));
            list.Gap(8f);
            list.CheckboxLabeled("[Debug] Mostrar metodos encontrados no log", ref DebugLog);
            list.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref WorkerThreadCount, "workerThreadCount",
                Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14));
            Scribe_Values.Look(ref DebugLog, "debugLog", false);
        }
    }
}
