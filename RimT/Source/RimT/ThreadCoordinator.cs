using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Verse;

namespace RimT
{
    /// <summary>
    /// Manages a fixed pool of background worker threads.
    /// Work items are queued via Schedule() and executed concurrently.
    /// The game tick calls WaitForAll() before the frame completes to ensure
    /// no stale data leaks between ticks.
    /// </summary>
    public static class ThreadCoordinator
    {
        // ── state ──────────────────────────────────────────────────────────────
        private static Thread[] _workers;
        private static BlockingCollection<Action> _queue;
        private static int _pendingCount;
        private static ManualResetEventSlim _allDone;
        private static volatile bool _shutdown;

        public static int ThreadCount => _workers?.Length ?? 0;
        public static bool IsInitialized => _workers != null;

        // ── init / shutdown ────────────────────────────────────────────────────
        public static void Initialize(int threadCount)
        {
            if (_workers != null) Shutdown();

            threadCount = Math.Max(1, Math.Min(threadCount, 14));
            _queue = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), 4096);
            _allDone = new ManualResetEventSlim(true);
            _pendingCount = 0;
            _shutdown = false;

            _workers = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                var t = new Thread(WorkerLoop)
                {
                    Name = $"RimT-Worker-{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal   // never starve Unity main thread
                };
                _workers[i] = t;
                t.Start();
            }
        }

        public static void Shutdown()
        {
            _shutdown = true;
            _queue?.CompleteAdding();
            if (_workers != null)
                foreach (var t in _workers)
                    t?.Join(500);
            _workers = null;
        }

        // ── public API ─────────────────────────────────────────────────────────
        /// <summary>
        /// Enqueue a work item. Safe to call from the main thread during tick.
        /// The action must NOT touch Unity objects or RimWorld collections that
        /// are not thread-safe. Results must be queued via MainThreadDispatcher.
        /// </summary>
        public static bool Schedule(Action work)
        {
            if (!IsInitialized || _shutdown || work == null) return false;
            try
            {
                Interlocked.Increment(ref _pendingCount);
                _allDone.Reset();
                _queue.TryAdd(WrapWork(work));
                return true;
            }
            catch
            {
                Interlocked.Decrement(ref _pendingCount);
                return false;
            }
        }

        /// <summary>
        /// Block main thread until all scheduled work for this tick is done.
        /// Call this once per tick BEFORE applying results.
        /// Times out after 50 ms to avoid hard freezes on overload.
        /// </summary>
        public static void WaitForAll(int timeoutMs = 50)
        {
            if (_pendingCount <= 0) return;
            _allDone.Wait(timeoutMs);
        }

        // ── internals ──────────────────────────────────────────────────────────
        private static Action WrapWork(Action inner)
        {
            return () =>
            {
                try { inner(); }
                catch (Exception ex)
                {
                    if (RimTMod.Settings?.DebugLog == true)
                        Log.Warning($"[RimT] Worker exception: {ex.Message}");
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pendingCount) <= 0)
                        _allDone.Set();
                }
            };
        }

        private static void WorkerLoop()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (_shutdown) break;
                try { work(); }
                catch { /* wrapped above, belt-and-suspenders */ }
            }
        }
    }
}
