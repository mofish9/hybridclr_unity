using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HybridCLR
{
    /// <summary>
    /// Incrementally prepares a manifest of hot-update types without forcing the
    /// whole manifest into one frame. The queue is intended to be driven from the
    /// main thread during loading, lobby, or another non-time-sensitive phase.
    /// </summary>
    public sealed class RuntimePrewarmQueue
    {
        private const int MaxNativeBatchSize = 16;
        private readonly Type[] _types;
        private readonly Type[] _nativeBatch = new Type[MaxNativeBatchSize];
        private double _estimatedMillisecondsPerType;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private Type _lastFailedType;

        /// <summary>
        /// Creates a queue and removes duplicate types while preserving input order.
        /// The input is copied, so it may be reused or changed after construction.
        /// </summary>
        /// <param name="types">Closed hot-update types used by the next user-visible path.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="types"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the sequence contains a null type.</exception>
        public RuntimePrewarmQueue(IEnumerable<Type> types)
        {
            if (types == null)
                throw new ArgumentNullException("types");

            var uniqueTypes = new List<Type>();
            var seenTypes = new HashSet<Type>();
            foreach (Type type in types)
            {
                if (type == null)
                    throw new ArgumentException("The prewarm manifest contains a null type.", "types");
                if (seenTypes.Add(type))
                    uniqueTypes.Add(type);
            }
            _types = uniqueTypes.ToArray();
        }

        /// <summary>Number of unique manifest types that have not been processed.</summary>
        public int RemainingCount
        {
            get { return _types.Length - _nextIndex; }
        }

        /// <summary>Number of unique types in the manifest.</summary>
        public int TotalCount
        {
            get { return _types.Length; }
        }

        /// <summary>Fraction of the manifest that has been attempted, in the range [0, 1].</summary>
        public float Progress
        {
            get { return _types.Length == 0 ? 1f : (float)_nextIndex / _types.Length; }
        }

        /// <summary>Number of types whose native preparation returned true.</summary>
        public int SucceededCount
        {
            get { return _succeededCount; }
        }

        /// <summary>Number of types whose native preparation returned false.</summary>
        public int FailedCount
        {
            get { return _failedCount; }
        }

        /// <summary>The most recent type for which preparation returned false, or null.</summary>
        public Type LastFailedType
        {
            get { return _lastFailedType; }
        }

        /// <summary>Whether every type in the manifest has been attempted.</summary>
        public bool IsComplete
        {
            get { return _nextIndex >= _types.Length; }
        }

        /// <summary>
        /// Processes as many types as fit in the supplied time budget. A zero budget
        /// still processes one type so callers always make progress. A type that
        /// returns false is recorded and does not stop subsequent types from running.
        /// Exceptions are intentionally propagated to expose runtime failures.
        /// </summary>
        /// <param name="budgetMilliseconds">Per-call budget. Must be finite and non-negative.</param>
        public RuntimePrewarmBatchResult Process(float budgetMilliseconds)
        {
            return Process(budgetMilliseconds, int.MaxValue);
        }

        /// <summary>
        /// Processes types under both a time budget and a type-count cap. The count
        /// cap is useful on devices whose timer resolution makes many small
        /// preparations appear to fit in one frame.
        /// </summary>
        /// <param name="budgetMilliseconds">Per-call budget. Must be finite and non-negative.</param>
        /// <param name="maxTypes">Maximum number of types to attempt. Must be positive.</param>
        public RuntimePrewarmBatchResult Process(float budgetMilliseconds, int maxTypes)
        {
            if (float.IsNaN(budgetMilliseconds) || float.IsInfinity(budgetMilliseconds) || budgetMilliseconds < 0f)
                throw new ArgumentOutOfRangeException("budgetMilliseconds", "The prewarm budget must be finite and non-negative.");
            if (maxTypes < 1)
                throw new ArgumentOutOfRangeException("maxTypes", "The prewarm type cap must be positive.");

            if (IsComplete)
                return new RuntimePrewarmBatchResult(0, 0, 0, 0d, RemainingCount);

            long startTimestamp = Stopwatch.GetTimestamp();
            int processed = 0;
            int succeeded = 0;
            int failed = 0;
            int nativeBatchLimit = budgetMilliseconds <= 0f ? 1 : MaxNativeBatchSize;
            while (!IsComplete && processed < maxTypes &&
                (processed == 0 || ElapsedMilliseconds(startTimestamp) < budgetMilliseconds))
            {
                int batchCount = Math.Min(nativeBatchLimit, Math.Min(maxTypes - processed, RemainingCount));
                if (budgetMilliseconds > 0f)
                {
                    if (_estimatedMillisecondsPerType <= 0d)
                    {
                        // Establish a conservative estimate without allowing the
                        // first frame to submit a burst of expensive types.
                        batchCount = 1;
                    }
                    else
                    {
                        double remainingBudget = budgetMilliseconds - ElapsedMilliseconds(startTimestamp);
                        double conservativePerType = _estimatedMillisecondsPerType * 1.25d;
                        int budgetBatchCount = remainingBudget > 0d
                            ? (int)Math.Floor(remainingBudget / conservativePerType)
                            : 1;
                        batchCount = Math.Min(batchCount, Math.Max(1, budgetBatchCount));
                    }
                }
                Array.Copy(_types, _nextIndex, _nativeBatch, 0, batchCount);
                long batchStarted = Stopwatch.GetTimestamp();
                int failureMask = RuntimeApi.PrewarmClassBatchResultMask(_nativeBatch, batchCount);
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    Type type = _nativeBatch[batchIndex];
                    if ((failureMask & (1 << batchIndex)) == 0)
                    {
                        succeeded++;
                        _succeededCount++;
                    }
                    else
                    {
                        failed++;
                        _failedCount++;
                        _lastFailedType = type;
                    }
                }
                _nextIndex += batchCount;
                processed += batchCount;
                UpdateEstimate(ElapsedMilliseconds(batchStarted), batchCount);
            }

            return new RuntimePrewarmBatchResult(
                processed,
                succeeded,
                failed,
                ElapsedMilliseconds(startTimestamp),
                RemainingCount);
        }

        private static double ElapsedMilliseconds(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        private void UpdateEstimate(double elapsedMilliseconds, int processedCount)
        {
            if (elapsedMilliseconds <= 0d || processedCount < 1)
                return;
            double sample = elapsedMilliseconds / processedCount;
            _estimatedMillisecondsPerType = _estimatedMillisecondsPerType <= 0d
                ? sample
                : (_estimatedMillisecondsPerType * 0.5d) + (sample * 0.5d);
        }
    }

    /// <summary>Statistics from one call to <see cref="RuntimePrewarmQueue.Process"/>.</summary>
    public struct RuntimePrewarmBatchResult
    {
        public RuntimePrewarmBatchResult(int processedCount, int succeededCount, int failedCount,
            double elapsedMilliseconds, int remainingCount)
        {
            ProcessedCount = processedCount;
            SucceededCount = succeededCount;
            FailedCount = failedCount;
            ElapsedMilliseconds = elapsedMilliseconds;
            RemainingCount = remainingCount;
        }

        public int ProcessedCount { get; private set; }
        public int SucceededCount { get; private set; }
        public int FailedCount { get; private set; }
        public double ElapsedMilliseconds { get; private set; }
        public int RemainingCount { get; private set; }
        public bool IsComplete { get { return RemainingCount == 0; } }
    }
}
