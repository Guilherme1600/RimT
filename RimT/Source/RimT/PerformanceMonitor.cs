using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimT
{
    /// <summary>
    /// Lightweight TPS/FPS monitor as a GameComponent.
    /// Toggle overlay with F10.
    /// Adaptively adjusts worker thread count based on measured TPS.
    /// </summary>
    public class RimTPerformanceMonitor : GameComponent
    {
        // ── rolling samples ────────────────────────────────────────────────────
        private readonly Queue<float> _frameTimeSamples = new Queue<float>(120);
        private readonly Queue<long>  _tickTimeSamples  = new Queue<long>(120);

        private float _avgFps;
        private float _avgTps;
        private long  _tickStart;
        private bool  _showOverlay;

        // ── adaptive throttle ─────────────────────────────────────────────────
        private int _nextAdaptCheck = 600;
        private const float TPS_LOW = 15f;
        private const float TPS_OK  = 30f;

        // ── GUI style (created lazily) ────────────────────────────────────────
        private GUIStyle _labelStyle;

        public RimTPerformanceMonitor(Game game) { }

        // ── Unity frame update ────────────────────────────────────────────────
        public override void GameComponentUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt < 1f)          // sanity: skip spikes > 1 s
            {
                _frameTimeSamples.Enqueue(dt);
                if (_frameTimeSamples.Count > 120) _frameTimeSamples.Dequeue();

                float sum = 0f;
                foreach (var s in _frameTimeSamples) sum += s;
                _avgFps = _frameTimeSamples.Count / sum;
            }

            // Toggle overlay — use KeyCode directly (from UnityEngine.CoreModule)
            if (Event.current != null
                && Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.F10)
            {
                _showOverlay = !_showOverlay;
                Event.current.Use();
            }
        }

        // ── game tick ─────────────────────────────────────────────────────────
        public override void GameComponentTick()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_tickStart != 0)
            {
                _tickTimeSamples.Enqueue(now - _tickStart);
                if (_tickTimeSamples.Count > 120) _tickTimeSamples.Dequeue();

                long total = 0;
                foreach (var s in _tickTimeSamples) total += s;
                float avgMs = (float)total / _tickTimeSamples.Count
                              / System.Diagnostics.Stopwatch.Frequency * 1000f;
                _avgTps = avgMs > 0f ? 1000f / avgMs : 0f;
            }
            _tickStart = now;

            // Adaptive thread scaling
            int tick = Find.TickManager.TicksGame;
            if (tick >= _nextAdaptCheck)
            {
                _nextAdaptCheck = tick + 600;
                AdaptThreadCount();
            }
        }

        // ── IMGUI overlay ─────────────────────────────────────────────────────
        public override void GameComponentOnGUI()
        {
            if (!_showOverlay) return;
            try
            {
                if (_labelStyle == null)
                {
                    _labelStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize  = 13,
                        fontStyle = FontStyle.Bold,
                        normal    = { textColor = Color.white }
                    };
                }

                string line1 = $"[RimT]  FPS: {_avgFps:F0}   TPS: {_avgTps:F0}   Threads: {ThreadCoordinator.ThreadCount}";
                string line2 = "Press F10 to hide";

                // Shadow
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.Label(new Rect(11f, 11f, 420f, 22f), line1, _labelStyle);
                GUI.Label(new Rect(11f, 29f, 420f, 18f), line2, _labelStyle);

                GUI.color = Color.white;
                GUI.Label(new Rect(10f, 10f, 420f, 22f), line1, _labelStyle);

                GUI.color = new Color(0.8f, 0.8f, 0.8f, 1f);
                GUI.Label(new Rect(10f, 28f, 420f, 18f), line2, _labelStyle);

                GUI.color = Color.white; // always reset
            }
            catch { /* never let GUI errors break the game */ }
        }

        // ── adaptive logic ────────────────────────────────────────────────────
        private void AdaptThreadCount()
        {
            if (_avgTps <= 0f) return;
            var s = RimTMod.Settings;
            if (s == null) return;

            if (_avgTps < TPS_LOW && s.WorkerThreadCount > 1)
            {
                s.WorkerThreadCount = Math.Max(1, s.WorkerThreadCount - 1);
                ThreadCoordinator.Initialize(s.WorkerThreadCount);
                if (s.DebugLog)
                    Log.Message($"[RimT] TPS baixo ({_avgTps:F1}), reduziu threads para {s.WorkerThreadCount}");
            }
            else if (_avgTps > TPS_OK && s.WorkerThreadCount < Environment.ProcessorCount - 1)
            {
                s.WorkerThreadCount = Math.Min(Environment.ProcessorCount - 1, s.WorkerThreadCount + 1);
                ThreadCoordinator.Initialize(s.WorkerThreadCount);
                if (s.DebugLog)
                    Log.Message($"[RimT] TPS bom ({_avgTps:F1}), aumentou threads para {s.WorkerThreadCount}");
            }
        }
    }
}
