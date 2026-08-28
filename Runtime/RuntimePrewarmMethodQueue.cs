using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HybridCLR
{
    /// <summary>
    /// Incrementally prepares an explicit set of methods during a loading phase.
    /// This is useful when a static call graph is much smaller than the set of
    /// methods declared by its types.
    /// </summary>
    public sealed class RuntimePrewarmMethodQueue
    {
        private readonly MethodInfo[] _methods;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private MethodInfo _lastFailedMethod;

        /// <summary>Creates a queue and removes duplicate methods while preserving input order.</summary>
        public RuntimePrewarmMethodQueue(IEnumerable<MethodInfo> methods)
        {
            if (methods == null)
                throw new ArgumentNullException("methods");

            var uniqueMethods = new List<MethodInfo>();
            var seenMethods = new HashSet<MethodInfo>();
            foreach (MethodInfo method in methods)
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
        public MethodInfo LastFailedMethod { get { return _lastFailedMethod; } }
        public bool IsComplete { get { return _nextIndex >= _methods.Length; } }

        /// <summary>Processes methods under a time budget.</summary>
        public RuntimePrewarmMethodBatchResult Process(float budgetMilliseconds)
        {
            return Process(budgetMilliseconds, int.MaxValue);
        }

        /// <summary>Processes methods under both a time budget and a method-count cap.</summary>
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
            do
            {
                MethodInfo method = _methods[_nextIndex];
                bool prepared = RuntimeApi.PrewarmMethod(method);
                _nextIndex++;
                processed++;
                if (prepared)
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
            while (!IsComplete && processed < maxMethods && ElapsedMilliseconds(startTimestamp) < budgetMilliseconds);

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
    }

    /// <summary>Statistics from one call to <see cref="RuntimePrewarmMethodQueue.Process"/>.</summary>
    public struct RuntimePrewarmMethodBatchResult
    {
        public RuntimePrewarmMethodBatchResult(int processedCount, int succeededCount, int failedCount,
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
