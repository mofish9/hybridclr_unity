using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.Scripting;

namespace HybridCLR
{
    [Preserve]
    public static class RuntimeApi
    {
#if UNITY_EDITOR
        // Keep editor-only shims' argument contract aligned with the native
        // internal calls. This matters for manifest validation tests and avoids
        // C# shift-count wraparound for invalid values.
        private static void ValidateBatch<T>(T[] values, int count, int maxCount)
        {
            if (values == null)
                throw new NullReferenceException();
            if (count < 0 || count > values.Length || (maxCount >= 0 && count > maxCount))
                throw new ArgumentOutOfRangeException("count");
        }
#endif

        /// <summary>
        /// load supplementary metadata assembly
        /// </summary>
        /// <param name="dllBytes"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
#if UNITY_EDITOR
        public static unsafe LoadImageErrorCode LoadMetadataForAOTAssembly(byte[] dllBytes, HomologousImageMode mode)
        {
            return LoadImageErrorCode.OK;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern LoadImageErrorCode LoadMetadataForAOTAssembly(byte[] dllBytes, HomologousImageMode mode);
#endif

        /// <summary>
        /// Legacy DHE entry point. It is retained for ABI compatibility, but
        /// the strict overload must be used because a snapshot hash is
        /// required to bind the MV to the player AOT image.
        /// </summary>
#if UNITY_EDITOR
        [Obsolete("Use the overload with aotSnapshotHash for DHE loading.")]
        public static LoadImageErrorCode LoadDifferentialHybridAssemblyWithMetaVersion(byte[] dllBytes, byte[] mvBytes)
        {
            return LoadImageErrorCode.DHE_MV_BAD_SNAPSHOT_HASH;
        }

        public static LoadImageErrorCode LoadDifferentialHybridAssemblyWithMetaVersion(byte[] dllBytes, byte[] mvBytes, byte[] aotSnapshotHash)
        {
            return LoadImageErrorCode.NOT_IMPLEMENT;
        }
#else
        [Obsolete("Use the overload with aotSnapshotHash for DHE loading.")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern LoadImageErrorCode LoadDifferentialHybridAssemblyWithMetaVersion(byte[] dllBytes, byte[] mvBytes);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern LoadImageErrorCode LoadDifferentialHybridAssemblyWithMetaVersion(byte[] dllBytes, byte[] mvBytes, byte[] aotSnapshotHash);
#endif

        /// <summary>Returns whether a loaded DHE MV marks this method as changed.</summary>
#if UNITY_EDITOR
        public static bool IsDifferentialMethodChanged(MethodInfo method)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool IsDifferentialMethodChanged(MethodInfo method);
#endif

        /// <summary>Returns diagnostic counters for DHE dispatch.</summary>
#if UNITY_EDITOR
        public static int GetDifferentialInterpreterEntryCount() { return 0; }
        public static int GetDifferentialAotBridgeCallCount() { return 0; }
        public static int GetDifferentialAotEntryCount() { return 0; }
        public static void ResetDifferentialDispatchCounters() { }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetDifferentialInterpreterEntryCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetDifferentialAotBridgeCallCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetDifferentialAotEntryCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ResetDifferentialDispatchCounters();
#endif

        /// <summary>
        /// initialize metadata and the interpreter entry point for one method.
        /// This is useful when a loading phase knows the exact methods that the
        /// next scene will invoke and a whole-class warmup would be too broad.
        /// The operation is idempotent and does not invoke the method.
        /// </summary>
        /// <param name="method">A method from a closed hot-update type.</param>
        /// <returns>true when the method is ready for its first call; false for an unsupported/open-generic method or a preparation failure.</returns>
#if UNITY_EDITOR
        public static bool PrewarmMethod(MethodInfo method)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmMethod(MethodInfo method);
#endif

        /// <summary>
        /// Initializes metadata and the interpreter entry point for a method or
        /// constructor without invoking it. This overload is used by generated
        /// call-graph manifests that include object construction edges.
        /// </summary>
        /// <param name="method">A closed method or constructor.</param>
        /// <returns>true when the member is ready for its first call; false for an unsupported/open-generic member or a preparation failure.</returns>
#if UNITY_EDITOR
        public static bool PrewarmMethodBase(MethodBase method)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmMethodBase(MethodBase method);
#endif

        /// <summary>
        /// Prepares a bounded batch of methods or constructors under one native
        /// preparation pass. Only the first <paramref name="count"/> entries are
        /// read; the array may be reused by a caller across frames.
        /// </summary>
        /// <param name="methods">Closed methods or constructors to prepare.</param>
        /// <param name="count">Number of leading entries to prepare.</param>
        /// <returns>true when every requested member was prepared.</returns>
#if UNITY_EDITOR
        public static bool PrewarmMethodBaseBatch(MethodBase[] methods, int count)
        {
            ValidateBatch(methods, count, -1);
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmMethodBaseBatch(MethodBase[] methods, int count);
#endif

        /// <summary>
        /// Prepares up to 32 methods and returns one failure bit per input. Bit
        /// <c>n</c> is set only when input <c>n</c> could not be prepared.
        /// </summary>
#if UNITY_EDITOR
        internal static int PrewarmMethodBaseBatchResultMask(MethodBase[] methods, int count)
        {
            ValidateBatch(methods, count, 32);
            return count >= 32 ? -1 : (1 << count) - 1;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int PrewarmMethodBaseBatchResultMask(MethodBase[] methods, int count);
#endif

        /// <summary>
        /// Prepares one non-generic hot-update method identified by its declaring
        /// type and metadata token. This avoids materializing the complete method
        /// list on runtimes that support per-slot method setup.
        /// </summary>
#if UNITY_EDITOR
        public static bool PrewarmMethodToken(Type declaringType, int metadataToken)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmMethodToken(Type declaringType, int metadataToken);
#endif

        /// <summary>
        /// Prepares the first <paramref name="count"/> type/token pairs in one
        /// native pass. This is an entry-first fast path; it does not promise
        /// that the declaring type's complete reflection method table is ready.
        /// </summary>
#if UNITY_EDITOR
        public static bool PrewarmMethodTokenBatch(Type[] declaringTypes, int[] metadataTokens, int count)
        {
            ValidateBatch(declaringTypes, count, -1);
            ValidateBatch(metadataTokens, count, -1);
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmMethodTokenBatch(Type[] declaringTypes, int[] metadataTokens, int count);
#endif

        /// <summary>Returns one failure bit per type/token pair for a batch of up to 32 entries.</summary>
#if UNITY_EDITOR
        internal static int PrewarmMethodTokenBatchResultMask(Type[] declaringTypes, int[] metadataTokens, int count)
        {
            ValidateBatch(declaringTypes, count, 32);
            ValidateBatch(metadataTokens, count, 32);
            return count >= 32 ? -1 : (1 << count) - 1;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int PrewarmMethodTokenBatchResultMask(Type[] declaringTypes, int[] metadataTokens, int count);
#endif

        /// <summary>
        /// prejit method to avoid the jit cost of first time running
        /// </summary>
        /// <param name="method"></param>
        /// <returns>return true if method is jited, return false if method can't be jited </returns>
        /// 
#if UNITY_EDITOR
        public static bool PreJitMethod(MethodInfo method)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PreJitMethod(MethodInfo method);
#endif

        /// <summary>
        /// prejit all methods of class to avoid the jit cost of first time running
        /// </summary>
        /// <param name="type"></param>
        /// <returns>return true if class is jited, return false if class can't be jited </returns>
#if UNITY_EDITOR
        public static bool PreJitClass(Type type)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PreJitClass(Type type);
#endif

        /// <summary>
        /// initialize the metadata and interpreter entry points for a class.
        /// Call this from a loading or lobby phase to move first-use work out of
        /// the first visible frame. The operation is idempotent and does not run
        /// the type initializer (static constructor).
        /// </summary>
        /// <param name="type">A closed hot-update class to initialize.</param>
        /// <returns>true when class metadata and all eligible interpreter methods were prepared; false for unsupported/open-generic types or a preparation failure.</returns>
#if UNITY_EDITOR
        public static bool PrewarmClass(Type type)
        {
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmClass(Type type);
#endif

        /// <summary>
        /// Prepares the first <paramref name="count"/> classes in one native
        /// call. The operation is equivalent to calling <see cref="PrewarmClass"/>
        /// for each entry and remains idempotent.
        /// </summary>
#if UNITY_EDITOR
        public static bool PrewarmClassBatch(Type[] types, int count)
        {
            ValidateBatch(types, count, -1);
            return false;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool PrewarmClassBatch(Type[] types, int count);
#endif

        /// <summary>Returns one failure bit per type for a batch of up to 32 entries.</summary>
#if UNITY_EDITOR
        internal static int PrewarmClassBatchResultMask(Type[] types, int count)
        {
            ValidateBatch(types, count, 32);
            return count >= 32 ? -1 : (1 << count) - 1;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int PrewarmClassBatchResultMask(Type[] types, int count);
#endif

        /// <summary>
        /// get the maximum number of StackObjects in the interpreter thread stack (size*8 represents the final memory size occupied
        /// </summary>
        /// <returns></returns>
        public static int GetInterpreterThreadObjectStackSize()
        {
            return GetRuntimeOption(RuntimeOptionId.InterpreterThreadObjectStackSize);
        }

        /// <summary>
        /// set the maximum number of StackObjects for the interpreter thread stack (size*8 represents the final memory size occupied)
        /// </summary>
        /// <param name="size"></param>
        public static void SetInterpreterThreadObjectStackSize(int size)
        {
            SetRuntimeOption(RuntimeOptionId.InterpreterThreadObjectStackSize, size);
        }


        /// <summary>
        /// get the number of interpreter thread function frames (sizeof(InterpreterFrame)*size represents the final memory size occupied)
        /// </summary>
        /// <returns></returns>
        public static int GetInterpreterThreadFrameStackSize()
        {
            return GetRuntimeOption(RuntimeOptionId.InterpreterThreadFrameStackSize);
        }

        /// <summary>
        /// set the number of interpreter thread function frames (sizeof(InterpreterFrame)*size represents the final memory size occupied)
        /// </summary>
        /// <param name="size"></param>
        public static void SetInterpreterThreadFrameStackSize(int size)
        {
            SetRuntimeOption(RuntimeOptionId.InterpreterThreadFrameStackSize, size);
        }


#if UNITY_EDITOR

        private static readonly Dictionary<RuntimeOptionId, int> s_runtimeOptions = new Dictionary<RuntimeOptionId, int>();

        /// <summary>
        /// set runtime option value
        /// </summary>
        /// <param name="optionId"></param>
        /// <param name="value"></param>
        public static void SetRuntimeOption(RuntimeOptionId optionId, int value)
        {
            s_runtimeOptions[optionId] = value;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SetRuntimeOption(RuntimeOptionId optionId, int value);
#endif

        /// <summary>
        /// get runtime option value
        /// </summary>
        /// <param name="optionId"></param>
        /// <returns></returns>
#if UNITY_EDITOR
        public static int GetRuntimeOption(RuntimeOptionId optionId)
        {
            if (s_runtimeOptions.TryGetValue(optionId, out var value))
            {
                return value;
            }
            return 0;
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetRuntimeOption(RuntimeOptionId optionId);
#endif
    }
}
