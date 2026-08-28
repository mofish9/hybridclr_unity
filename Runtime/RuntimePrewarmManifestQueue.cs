using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HybridCLR
{
    /// <summary>
    /// Resolves and prepares a type-name manifest incrementally. Construction is
    /// allocation-only; type lookup, generic construction, and native metadata
    /// preparation happen in <see cref="Process"/> so callers can keep each
    /// loading-frame within a budget.
    /// </summary>
    public sealed class RuntimePrewarmManifestQueue
    {
        private const int MaxNativeBatchSize = 16;
        private readonly Assembly _assembly;
        private readonly string[] _typeNames;
        private readonly Type[] _nativeBatch = new Type[MaxNativeBatchSize];
        private double _estimatedMillisecondsPerType;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private Type _lastFailedType;

        internal RuntimePrewarmManifestQueue(Assembly assembly, IEnumerable<string> typeNames)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (typeNames == null)
                throw new ArgumentNullException("typeNames");

            var uniqueNames = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("The prewarm manifest contains an empty type name.", "typeNames");
                if (seenNames.Add(typeName))
                    uniqueNames.Add(typeName);
            }
            _assembly = assembly;
            _typeNames = uniqueNames.ToArray();
        }

        public int RemainingCount { get { return _typeNames.Length - _nextIndex; } }
        public int TotalCount { get { return _typeNames.Length; } }
        public float Progress { get { return _typeNames.Length == 0 ? 1f : (float)_nextIndex / _typeNames.Length; } }
        public int SucceededCount { get { return _succeededCount; } }
        public int FailedCount { get { return _failedCount; } }
        public string LastFailedTypeName { get; private set; }
        public Type LastFailedType { get { return _lastFailedType; } }
        public bool IsComplete { get { return _nextIndex >= _typeNames.Length; } }

        public RuntimePrewarmBatchResult Process(float budgetMilliseconds)
        {
            return Process(budgetMilliseconds, int.MaxValue);
        }

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
                        // The first call establishes the estimate and deliberately
                        // resolves one entry to avoid an unbounded first-frame burst.
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

                long batchStarted = Stopwatch.GetTimestamp();
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    string typeName = _typeNames[_nextIndex + batchIndex];
                    Type type = RuntimePrewarmManifest.ResolveType(_assembly, typeName);
                    if (type == null)
                        throw new InvalidOperationException("The prewarm manifest type was not found: " + typeName);
                    _nativeBatch[batchIndex] = type;
                }

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
                        LastFailedTypeName = _typeNames[_nextIndex + batchIndex];
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
}
