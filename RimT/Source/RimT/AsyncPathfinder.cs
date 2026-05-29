using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimT
{
    public static class AsyncPathfinder
    {
        private static readonly ConcurrentDictionary<long, bool> _inFlight
            = new ConcurrentDictionary<long, bool>();

        private static readonly ConcurrentDictionary<long, PawnPath> _results
            = new ConcurrentDictionary<long, PawnPath>();

        // FindPath method — discovered at runtime to handle signature changes
        private static MethodInfo _findPathMethod;
        private static readonly ConcurrentDictionary<int, PathFinder> _pathFinders
            = new ConcurrentDictionary<int, PathFinder>();

        private static int _lastCleanup = 0;

        private static MethodInfo GetFindPathMethod()
        {
            if (_findPathMethod != null) return _findPathMethod;
            // Try all overloads — pick the one with IntVec3, LocalTargetInfo, TraverseParms
            foreach (var m in typeof(PathFinder).GetMethods())
            {
                if (m.Name != "FindPath") continue;
                var p = m.GetParameters();
                if (p.Length >= 3
                    && p[0].ParameterType == typeof(IntVec3)
                    && p[1].ParameterType == typeof(LocalTargetInfo)
                    && p[2].ParameterType == typeof(TraverseParms))
                {
                    _findPathMethod = m;
                    break;
                }
            }
            return _findPathMethod;
        }

        public static void Cleanup()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - _lastCleanup < 300) return;
            _lastCleanup = now;
            _inFlight.Clear();
            foreach (var kvp in _results)
            {
                try { if (kvp.Value != PawnPath.NotFound) kvp.Value?.ReleaseToPool(); }
                catch { }
            }
            _results.Clear();
        }

        public static bool TryGetCachedPath(long key, out PawnPath path)
            => _results.TryRemove(key, out path);

        public static bool IsInFlight(long key) => _inFlight.ContainsKey(key);

        public static void RequestAsync(long key, Map map,
            IntVec3 start, LocalTargetInfo dest,
            TraverseParms traverseParms, PathEndMode peMode)
        {
            if (_inFlight.ContainsKey(key)) return;
            _inFlight[key] = true;

            int mapId = map.uniqueID;

            ThreadCoordinator.Schedule(() =>
            {
                try
                {
                    if (!_pathFinders.TryGetValue(mapId, out var pf))
                    {
                        pf = new PathFinder(map);
                        _pathFinders[mapId] = pf;
                    }

                    var findPath = GetFindPathMethod();
                    if (findPath == null)
                    {
                        _inFlight.TryRemove(key, out _);
                        MainThreadDispatcher.Post(() => _results[key] = PawnPath.NotFound);
                        return;
                    }

                    // Build args based on parameter count
                    var parms = findPath.GetParameters();
                    object[] args;
                    if (parms.Length == 4)
                        args = new object[] { start, dest, traverseParms, peMode };
                    else if (parms.Length == 3)
                        args = new object[] { start, dest, traverseParms };
                    else
                        args = new object[] { start, dest, traverseParms, peMode };

                    var path = findPath.Invoke(pf, args) as PawnPath;

                    MainThreadDispatcher.Post(() =>
                    {
                        _results[key] = path ?? PawnPath.NotFound;
                        _inFlight.TryRemove(key, out _);
                    });
                }
                catch
                {
                    _inFlight.TryRemove(key, out _);
                    MainThreadDispatcher.Post(() => _results[key] = PawnPath.NotFound);
                }
            });
        }

        public static long MakeKey(IntVec3 start, LocalTargetInfo dest,
            TraverseParms tp, PathEndMode peMode)
        {
            unchecked
            {
                long h = start.x * 73856093L ^ start.z * 19349663L;
                h ^= (long)dest.Cell.x * 83492791L ^ (long)dest.Cell.z * 15485863L;
                h ^= (long)tp.mode * 31L ^ (long)peMode * 17L;
                if (tp.pawn != null) h ^= tp.pawn.thingIDNumber * 1000003L;
                return h;
            }
        }
    }

}