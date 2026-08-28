using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HybridCLR
{
    /// <summary>Identifies a non-generic method by declaring type and metadata token.</summary>
    public sealed class RuntimePrewarmMethodToken
    {
        public RuntimePrewarmMethodToken(Type declaringType, int metadataToken)
        {
            if (declaringType == null)
                throw new ArgumentNullException("declaringType");
            if (metadataToken <= 0)
                throw new ArgumentOutOfRangeException("metadataToken");
            DeclaringType = declaringType;
            MetadataToken = metadataToken;
        }

        public Type DeclaringType { get; private set; }
        public int MetadataToken { get; private set; }
    }

    /// <summary>
    /// Budgeted queue for token-addressed methods. Use this when the next user
    /// visible path has a known method graph and does not require exhaustive
    /// reflection over the same declaring types.
    /// </summary>
    public sealed class RuntimePrewarmMethodTokenQueue
    {
        private const int MaxNativeBatchSize = 32;
        private readonly RuntimePrewarmMethodToken[] _methods;
        private readonly Type[] _nativeTypes = new Type[MaxNativeBatchSize];
        private readonly int[] _nativeTokens = new int[MaxNativeBatchSize];
        private double _estimatedMillisecondsPerMethod;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private RuntimePrewarmMethodToken _lastFailedMethod;

        public RuntimePrewarmMethodTokenQueue(IEnumerable<RuntimePrewarmMethodToken> methods)
        {
            if (methods == null)
                throw new ArgumentNullException("methods");

            var uniqueMethods = new List<RuntimePrewarmMethodToken>();
            var seenTokensByType = new Dictionary<Type, HashSet<int>>();
            foreach (RuntimePrewarmMethodToken method in methods)
            {
                if (method == null)
                    throw new ArgumentException("The prewarm token manifest contains a null method.", "methods");
                HashSet<int> seenTokens;
                if (!seenTokensByType.TryGetValue(method.DeclaringType, out seenTokens))
                {
                    seenTokens = new HashSet<int>();
                    seenTokensByType.Add(method.DeclaringType, seenTokens);
                }
                if (seenTokens.Add(method.MetadataToken))
                    uniqueMethods.Add(method);
            }
            _methods = uniqueMethods.ToArray();
        }

        public int RemainingCount { get { return _methods.Length - _nextIndex; } }
        public int TotalCount { get { return _methods.Length; } }
        public float Progress { get { return _methods.Length == 0 ? 1f : (float)_nextIndex / _methods.Length; } }
        public int SucceededCount { get { return _succeededCount; } }
        public int FailedCount { get { return _failedCount; } }
        public RuntimePrewarmMethodToken LastFailedMethod { get { return _lastFailedMethod; } }
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
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    RuntimePrewarmMethodToken method = _methods[_nextIndex + batchIndex];
                    _nativeTypes[batchIndex] = method.DeclaringType;
                    _nativeTokens[batchIndex] = method.MetadataToken;
                }

                long batchStarted = Stopwatch.GetTimestamp();
                int failureMask = RuntimeApi.PrewarmMethodTokenBatchResultMask(_nativeTypes, _nativeTokens, batchCount);
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    RuntimePrewarmMethodToken method = _methods[_nextIndex + batchIndex];
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
