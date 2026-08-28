using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HybridCLR
{
    /// <summary>Budgeted queue for methods and constructors from a static call graph.</summary>
    public sealed class RuntimePrewarmMethodBaseQueue
    {
        private const int MaxNativeBatchSize = 32;
        private readonly MethodBase[] _methods;
        private readonly MethodBase[] _nativeBatch = new MethodBase[MaxNativeBatchSize];
        private double _estimatedMillisecondsPerMethod;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private MethodBase _lastFailedMethod;

        public RuntimePrewarmMethodBaseQueue(IEnumerable<MethodBase> methods)
        {
            if (methods == null)
                throw new ArgumentNullException("methods");

            var uniqueMethods = new List<MethodBase>();
            var seenMethods = new HashSet<MethodBase>();
            foreach (MethodBase method in methods)
            {
                if (method == null)
                    throw new ArgumentException("The prewarm method manifest contains a null method.", "methods");
                if (seenMethods.Add(method))
                    uniqueMethods.Add(method);
            }
            _methods = uniqueMethods.ToArray();
        }

        public int RemainingCount { get { return _methods.Length - _nextIndex; } }
        public int TotalCount { get { return _methods.Length; } }
        public float Progress { get { return _methods.Length == 0 ? 1f : (float)_nextIndex / _methods.Length; } }
        public int SucceededCount { get { return _succeededCount; } }
        public int FailedCount { get { return _failedCount; } }
        public MethodBase LastFailedMethod { get { return _lastFailedMethod; } }
        public bool IsComplete { get { return _nextIndex >= _methods.Length; } }

        public RuntimePrewarmMethodBatchResult Process(float budgetMilliseconds)
        {
            return Process(budgetMilliseconds, int.MaxValue);
        }

        public RuntimePrewarmMethodBatchResult Process(float budgetMilliseconds, int maxMethods)
        {
            if (float.IsNaN(budgetMilliseconds) || float.IsInfinity(budgetMilliseconds) || budgetMilliseconds < 0f)
                throw new ArgumentOutOfRangeException("budgetMilliseconds", "The prewarm budget must be finite and non-negative.");
            if (maxMethods < 1)
                throw new ArgumentOutOfRangeException("maxMethods", "The prewarm method cap must be positive.");

            if (IsComplete)
                return new RuntimePrewarmMethodBatchResult(0, 0, 0, 0d, RemainingCount);

            long startTimestamp = Stopwatch.GetTimestamp();
            int processed = 0;
            int succeeded = 0;
            int failed = 0;
            int nativeBatchLimit = budgetMilliseconds <= 0f ? 1 : MaxNativeBatchSize;
            while (!IsComplete && processed < maxMethods &&
                (processed == 0 || ElapsedMilliseconds(startTimestamp) < budgetMilliseconds))
            {
                int batchCount = Math.Min(nativeBatchLimit, Math.Min(maxMethods - processed, RemainingCount));
                if (budgetMilliseconds > 0f)
                {
                    if (_estimatedMillisecondsPerMethod <= 0d)
                    {
                        batchCount = 1;
                    }
                    else
                    {
                        double remainingBudget = budgetMilliseconds - ElapsedMilliseconds(startTimestamp);
                        double conservativePerMethod = _estimatedMillisecondsPerMethod * 1.25d;
                        int budgetBatchCount = remainingBudget > 0d
                            ? (int)Math.Floor(remainingBudget / conservativePerMethod)
                            : 1;
                        batchCount = Math.Min(batchCount, Math.Max(1, budgetBatchCount));
                    }
                }
                Array.Copy(_methods, _nextIndex, _nativeBatch, 0, batchCount);
                long batchStarted = Stopwatch.GetTimestamp();
                int failureMask = RuntimeApi.PrewarmMethodBaseBatchResultMask(_nativeBatch, batchCount);
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    MethodBase method = _nativeBatch[batchIndex];
                    if ((failureMask & (1 << batchIndex)) == 0)
                    {
                        succeeded++;
                        _succeededCount++;
                    }
                    else
                    {
                        failed++;
                        _failedCount++;
                        _lastFailedMethod = method;
                    }
                }
                _nextIndex += batchCount;
                processed += batchCount;
                UpdateEstimate(ElapsedMilliseconds(batchStarted), batchCount);
            }

            return new RuntimePrewarmMethodBatchResult(
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
            _estimatedMillisecondsPerMethod = _estimatedMillisecondsPerMethod <= 0d
                ? sample
                : (_estimatedMillisecondsPerMethod * 0.5d) + (sample * 0.5d);
        }
    }
}
