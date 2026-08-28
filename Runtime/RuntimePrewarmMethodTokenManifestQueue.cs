using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HybridCLR
{
    /// <summary>
    /// Resolves token descriptors and prewarms them incrementally. Construction
    /// only copies and validates the descriptor shape; assembly type lookup is
    /// charged to the same budget as native preparation in <see cref="Process"/>.
    /// </summary>
    public sealed class RuntimePrewarmMethodTokenManifestQueue
    {
        private const int MaxNativeBatchSize = 32;
        private readonly Assembly _assembly;
        private readonly RuntimePrewarmMethodDescriptor[] _descriptors;
        private readonly Type[] _nativeTypes = new Type[MaxNativeBatchSize];
        private readonly int[] _nativeTokens = new int[MaxNativeBatchSize];
        private readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private double _estimatedMillisecondsPerMethod;
        private int _nextIndex;
        private int _succeededCount;
        private int _failedCount;
        private RuntimePrewarmMethodToken _lastFailedMethod;

        internal RuntimePrewarmMethodTokenManifestQueue(Assembly assembly,
            IEnumerable<RuntimePrewarmMethodDescriptor> descriptors)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (descriptors == null)
                throw new ArgumentNullException("descriptors");

            var copied = new List<RuntimePrewarmMethodDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (RuntimePrewarmMethodDescriptor descriptor in descriptors)
            {
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.DeclaringTypeName) ||
                    descriptor.MetadataToken <= 0)
                {
                    throw new ArgumentException("The prewarm token manifest contains an invalid descriptor.", "descriptors");
                }

                // Descriptor names are the stable manifest identity. Do not use
                // Type.AssemblyQualifiedName here because types are intentionally
                // unresolved at construction time and dynamic types may have no
                // assembly-qualified name.
                string key = descriptor.DeclaringTypeName + "\u001f" + descriptor.MetadataToken;
                if (seen.Add(key))
                    copied.Add(descriptor);
            }
            _assembly = assembly;
            _descriptors = copied.ToArray();
        }

        public int RemainingCount { get { return _descriptors.Length - _nextIndex; } }
        public int TotalCount { get { return _descriptors.Length; } }
        public float Progress { get { return _descriptors.Length == 0 ? 1f : (float)_nextIndex / _descriptors.Length; } }
        public int SucceededCount { get { return _succeededCount; } }
        public int FailedCount { get { return _failedCount; } }
        public RuntimePrewarmMethodToken LastFailedMethod { get { return _lastFailedMethod; } }
        public bool IsComplete { get { return _nextIndex >= _descriptors.Length; } }

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
                        // The first budget window is deliberately one item so a
                        // cold type lookup cannot turn into a large burst.
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

                long batchStarted = Stopwatch.GetTimestamp();
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    RuntimePrewarmMethodDescriptor descriptor = _descriptors[_nextIndex + batchIndex];
                    Type declaringType;
                    if (!_typeCache.TryGetValue(descriptor.DeclaringTypeName, out declaringType))
                    {
                        declaringType = RuntimePrewarmManifest.ResolveType(_assembly, descriptor.DeclaringTypeName);
                        if (declaringType != null)
                            _typeCache.Add(descriptor.DeclaringTypeName, declaringType);
                    }
                    if (declaringType == null)
                        throw new InvalidOperationException("The prewarm token declaring type was not found: " + descriptor.DeclaringTypeName);
                    _nativeTypes[batchIndex] = declaringType;
                    _nativeTokens[batchIndex] = descriptor.MetadataToken;
                }

                int failureMask = RuntimeApi.PrewarmMethodTokenBatchResultMask(_nativeTypes, _nativeTokens, batchCount);
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    RuntimePrewarmMethodDescriptor descriptor = _descriptors[_nextIndex + batchIndex];
                    if ((failureMask & (1 << batchIndex)) == 0)
                    {
                        succeeded++;
                        _succeededCount++;
                    }
                    else
                    {
                        failed++;
                        _failedCount++;
                        _lastFailedMethod = new RuntimePrewarmMethodToken(_nativeTypes[batchIndex], descriptor.MetadataToken);
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
