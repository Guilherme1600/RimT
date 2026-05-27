using System.Collections.Generic;
using Verse;

namespace RimT
{
    /// <summary>
    /// Distribui pawns em buckets para espalhar trabalho pesado por vários ticks.
    /// Usado pelos patches em Patches.cs para decidir se um pawn deve ser processado neste tick.
    /// </summary>
    public static class PawnBatchScheduler
    {
        private static readonly Dictionary<int, int> _pawnBucket = new Dictionary<int, int>(512);
        private static int _assignCounter;

        public static void Reset()
        {
            _pawnBucket.Clear();
            _assignCounter = 0;
        }

        public static int GetBucket(Pawn pawn, int bucketCount = 4)
        {
            if (!_pawnBucket.TryGetValue(pawn.thingIDNumber, out int bucket))
            {
                bucket = _assignCounter++ % bucketCount;
                _pawnBucket[pawn.thingIDNumber] = bucket;
            }
            return bucket;
        }

        /// <summary>
        /// Retorna true se este pawn deve correr trabalho pesado neste tick.
        /// Distribui a carga por tickInterval ticks consecutivos.
        /// </summary>
        public static bool ShouldRunHeavyWork(Pawn pawn, int tickInterval = 4)
        {
            int bucket = GetBucket(pawn, tickInterval);
            return Find.TickManager.TicksGame % tickInterval == bucket;
        }
    }
}
