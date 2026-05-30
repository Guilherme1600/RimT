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
        public static readonly Harmony Harmony = new Harmony("rimT.multithreaded");

        static RimTMod()
        {
            try
            {
                Settings = LoadedModManager.GetMod<RimTModDef>()?.GetSettings<RimTSettings>()
                           ?? new RimTSettings();

                ThreadCoordinator.Initialize(Settings.WorkerThreadCount);
                MainThreadDispatcher.Initialize();

                // Core — always active
                SafePatch(typeof(Patch_Game_UpdatePlay));

                // Job throttles — optional
                if (Settings.EnableJobThrottle)
                {
                    SafePatch(typeof(Patch_NeedsTracker));
                    SafePatch(typeof(Patch_MindState));
                    SafePatch(typeof(Patch_MentalBreaker));
                    SafePatch(typeof(Patch_HealthTracker));
                    SafePatch(typeof(Patch_JobGiver_Work));
                }

                // Reachability memory cache — optional
                if (Settings.EnableReachabilityCache)
                    SafePatch(typeof(Patch_Reachability_Cache));
                SafePatch(typeof(Patch_WorldPawnsTick));
                SafePatch(typeof(Patch_WorldObjectsTick));
                SafePatch(typeof(Patch_MapPostTick));

                // Async pathfinder — optional
                if (Settings.EnableAsyncPath)
                    SafePatch(typeof(Patch_PathFollower_StartPath));

                // Render throttle — optional
                if (Settings.EnableRenderThrottle)
                {
                    SafePatch(typeof(Patch_ColonistBar_Dirty));
                    SafePatch(typeof(Patch_PawnRenderer));

                }

                Log.Message($"[RimT] Iniciado — {Settings.WorkerThreadCount} threads.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimT] Falha: {ex}");
            }
        }

        public static void SafePatch(Type patchClass)
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
        private RimTSettings _s;
        public RimTModDef(ModContentPack content) : base(content)
            => _s = GetSettings<RimTSettings>();
        public override void DoSettingsWindowContents(Rect inRect)
            => _s.DoWindowContents(inRect);
        public override string SettingsCategory() => "RimT";
    }

    public class RimTSettings : ModSettings
    {
        // Threads
        public int WorkerThreadCount = Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14);

        // Adaptive tick rate — divisor aplicado a todos os ticks do jogo
        // 1 = normal, 2 = metade, 5 = 1/5 dos ticks
        public int TickRateDivisor = 1;

        // Toggles
        public bool EnableJobThrottle       = true;
        public bool EnableReachabilityCache = true;
        public bool EnableAsyncPath         = true;
        public bool EnableRenderThrottle    = true;

        // Render throttle intensidade
        public int ColonistBarInterval  = 5;   // frames entre rebuilds da barra
        public int PawnRenderInterval   = 3;   // frames entre renders de non-colonos fora de vista

        // Job throttle intervals
        public int VisitorJobInterval  = 1;   // 1 = vanilla (visitantes sempre reactivos)
        public int EnemyJobInterval    = 1;   // 1 = vanilla (inimigos sempre reactivos)
        public int AnimalJobInterval   = 30;  // ticks entre job scan de animais
        public int ColonistJobInterval = 1;   // colonos: sempre vanilla

        // Enemy activation radius — inimigos fora deste raio ficam "adormecidos"
        public bool EnableEnemyRadius  = false;
        public int  EnemyActiveRadius  = 60;  // células de raio

        public bool DebugLog = false;

        public void DoWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            // ── Threads ──────────────────────────────────────────────────────
            list.Label($"Worker threads: {WorkerThreadCount}   (CPUs: {Environment.ProcessorCount})");
            list.Label("Mais threads = mais TPS. Deixa 1-2 livres para o sistema.");
            WorkerThreadCount = (int)list.Slider(WorkerThreadCount, 1, Math.Max(1, Environment.ProcessorCount - 1));

            list.GapLine();

            // ── Adaptive tick rate ────────────────────────────────────────────
            list.Label($"Tick Rate Divisor: {TickRateDivisor}x   (1 = normal, 20 = 1/20 dos ticks calculados)");
            list.Label("Reduz calculos por tick. Aumenta TPS mas pode deixar pawns mais lentos.");
            TickRateDivisor = (int)list.Slider(TickRateDivisor, 1, 20);

            list.GapLine();

            // ── Job throttles ─────────────────────────────────────────────────
            list.CheckboxLabeled("Throttle de Jobs (Needs/Health/MindState/JobGiver)",
                ref EnableJobThrottle,
                "Reduz frequencia de calculos de trabalho para non-colonos. Pode deixar NPCs menos reactivos.");

            list.GapLine();

            // ── Reachability cache ────────────────────────────────────────────
            list.CheckboxLabeled("Memoria de Pathfinding (Reachability Cache)",
                ref EnableReachabilityCache,
                "Guarda em memoria se e possivel ir de A a B. Evita recalculos durante 120 ticks.");

            // ── Async path ────────────────────────────────────────────────────
            list.CheckboxLabeled("Pathfinding Assincrono (non-colonos)",
                ref EnableAsyncPath,
                "Calcula paths de NPCs em threads paralelas. 1 tick de delay mas liberta a main thread.");

            list.GapLine();

            // ── Render throttle ───────────────────────────────────────────────
            list.CheckboxLabeled("Throttle de Renderizacao",
                ref EnableRenderThrottle,
                "Limita actualizacoes da barra de colonos e render de pawns fora de vista.");

            if (EnableRenderThrottle)
            {
                list.Label($"  Intervalo da barra de colonos: {ColonistBarInterval} frames");
                ColonistBarInterval = (int)list.Slider(ColonistBarInterval, 1, 30);

                list.Label($"  Intervalo de render de NPCs fora de vista: {PawnRenderInterval} frames");
                PawnRenderInterval = (int)list.Slider(PawnRenderInterval, 1, 10);
            }

            list.GapLine();

            // ── Job intervals ─────────────────────────────────────────────────
            if (EnableJobThrottle)
            {
                list.Label("Intervalos de Job (ticks entre cada calculo de trabalho):");
                list.Label($"  Colonos sem job: {ColonistJobInterval} ticks  (1=vanilla, 3=recomendado)");
                ColonistJobInterval = (int)list.Slider(ColonistJobInterval, 1, 10);

                list.Label($"  Visitantes: {VisitorJobInterval} ticks  (1=vanilla, 5-10=recomendado)");
                VisitorJobInterval = (int)list.Slider(VisitorJobInterval, 1, 60);

                list.Label($"  Inimigos: {EnemyJobInterval} ticks  (1=vanilla, 5-10=recomendado)");
                EnemyJobInterval = (int)list.Slider(EnemyJobInterval, 1, 60);

                list.Label($"  Animais selvagens: {AnimalJobInterval} ticks  (1=vanilla, 30-60=recomendado)");
                AnimalJobInterval = (int)list.Slider(AnimalJobInterval, 1, 120);

                list.GapLine();
                list.CheckboxLabeled("Inimigos so atacam dentro de um raio (evita ataques surpresa de longe)",
                    ref EnableEnemyRadius);
                if (EnableEnemyRadius)
                {
                    list.Label($"  Raio de activacao de inimigos: {EnemyActiveRadius} celulas");
                    EnemyActiveRadius = (int)list.Slider(EnemyActiveRadius, 10, 150);
                }
            }

            list.GapLine();
            list.CheckboxLabeled("[Debug] Log verbose", ref DebugLog);

            list.Gap(8f);
            list.Label("Nota: alteracoes de toggles requerem reinicio do jogo.");
            list.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref WorkerThreadCount,      "workerThreadCount",
                Mathf.Clamp(Environment.ProcessorCount - 2, 2, 14));
            Scribe_Values.Look(ref TickRateDivisor,        "tickRateDivisor",        1);
            Scribe_Values.Look(ref EnableJobThrottle,      "enableJobThrottle",      true);
            Scribe_Values.Look(ref EnableReachabilityCache,"enableReachabilityCache",true);
            Scribe_Values.Look(ref EnableAsyncPath,        "enableAsyncPath",        true);
            Scribe_Values.Look(ref EnableRenderThrottle,   "enableRenderThrottle",   true);
            Scribe_Values.Look(ref ColonistBarInterval,    "colonistBarInterval",    5);
            Scribe_Values.Look(ref PawnRenderInterval,     "pawnRenderInterval",     3);
            Scribe_Values.Look(ref VisitorJobInterval,     "visitorJobInterval",     15);
            Scribe_Values.Look(ref EnemyJobInterval,       "enemyJobInterval",       10);
            Scribe_Values.Look(ref AnimalJobInterval,      "animalJobInterval",      60);
            Scribe_Values.Look(ref ColonistJobInterval,    "colonistJobInterval",    3);
            Scribe_Values.Look(ref EnableEnemyRadius,      "enableEnemyRadius",      false);
            Scribe_Values.Look(ref EnemyActiveRadius,      "enemyActiveRadius",      60);
            Scribe_Values.Look(ref DebugLog,               "debugLog",               false);
        }
    }
}
