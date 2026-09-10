using System;
using System.Linq;
using System.Threading;

namespace HybridCLR
{
    public enum DheLoadState { NotLoaded, Loading, Rejected, Ready, RestartRequired }

    public static partial class DheRuntime
    {
        private static int loadBusy, nativeTouched, metadataCommitted, loadState, nativeLoadPhase;
        private static bool trackedNativeBatch;
        private static string lastLoadError, lastNativeException;
        private delegate bool LoadOperation(out LoadImageErrorCode code, out string error);
        private delegate bool ConfigureOperation(out string error);

        public static DheLoadState LoadState => Volatile.Read(ref loadBusy) != 0
            ? DheLoadState.Loading : (DheLoadState)Volatile.Read(ref loadState);
        public static bool RestartRequired => Volatile.Read(ref loadState) == (int)DheLoadState.RestartRequired;
        /// <summary>Confirmed publication after the synchronous native call has returned or thrown.</summary>
        public static bool MetadataCommitted => Volatile.Read(ref metadataCommitted) != 0;
        public static string LastLoadError => Volatile.Read(ref lastLoadError) ?? string.Empty;

        /// <summary>Clears only a plan that has not touched native metadata.</summary>
        public static void Reset()
        {
            if (Interlocked.CompareExchange(ref loadBusy, 1, 0) != 0)
                throw new InvalidOperationException("DHE load/configuration is in progress.");
            try
            {
                if (Volatile.Read(ref nativeTouched) != 0 || RestartRequired)
                    throw new InvalidOperationException("DHE native metadata cannot be reset. Restart the process to select another resource.");
                ResetCore();
                lastLoadError = lastNativeException = null;
                Volatile.Write(ref loadState, (int)DheLoadState.NotLoaded);
            }
            finally { Volatile.Write(ref loadBusy, 0); }
        }

        private static bool Configure(ConfigureOperation action, out string error)
        {
            if (Interlocked.CompareExchange(ref loadBusy, 1, 0) != 0)
            { error = "DHE load/configuration is in progress."; return false; }
            try
            {
                if (Volatile.Read(ref nativeTouched) != 0 || RestartRequired)
                { error = "DHE native metadata already exists. Restart the process before changing the resource plan."; return false; }
                return action(out error);
            }
            finally { Volatile.Write(ref loadBusy, 0); }
        }

        public static bool Initialize(IDheRuntimeAssetProvider provider, DheRuntimeIdentity buildIdentity,
            out string error, string planAssetPath = PlanAssetPath, string runtimeAssetRoot = DefaultAssetRoot,
            bool enableValidationProbes = false) => Configure((out string message) =>
                InitializeCore(provider, buildIdentity, out message, planAssetPath, runtimeAssetRoot, enableValidationProbes), out error);

        public static bool InitializeFromResourceUpdate(IDheRuntimeAssetProvider provider, DheRuntimeIdentity buildIdentity,
            string manifestAssetPath, out string error, string runtimeAssetRoot = DefaultAssetRoot,
            bool enableValidationProbes = false) => Configure((out string message) =>
                InitializeFromResourceUpdateCore(provider, buildIdentity, manifestAssetPath, out message,
                    runtimeAssetRoot, enableValidationProbes), out error);

        public static bool LoadCurrentAssemblyImages(string[] assemblyNames, byte[][] currentDlls,
            out LoadImageErrorCode code, out string error) => ExecuteLoad(assemblyNames, currentDlls,
                (out LoadImageErrorCode result, out string message) => LoadCurrentAssemblyImagesCore(assemblyNames, currentDlls, out result, out message), out code, out error);

        public static bool LoadAssemblyImages(string[] assemblyNames, byte[][] currentDlls,
            out LoadImageErrorCode code, out string error) => ExecuteLoad(assemblyNames, currentDlls,
                (out LoadImageErrorCode result, out string message) => LoadAssemblyImagesCore(assemblyNames, currentDlls, out result, out message), out code, out error);

        public static bool LoadAssemblyImage(string assemblyName, byte[] currentDll,
            out LoadImageErrorCode code, out string error) => ExecuteLoad(new[] { assemblyName }, new[] { currentDll },
                (out LoadImageErrorCode result, out string message) => LoadAssemblyImageCore(assemblyName, currentDll, out result, out message), out code, out error);

        private static bool ExecuteLoad(string[] names, byte[][] dlls, LoadOperation action,
            out LoadImageErrorCode code, out string error)
        {
            if (Interlocked.CompareExchange(ref loadBusy, 1, 0) != 0)
            { code = LoadImageErrorCode.DHE_LOAD_IN_PROGRESS; error = "DHE load/configuration is in progress."; return false; }
            try
            {
                if (RestartRequired)
                { code = LoadImageErrorCode.DHE_RESTART_REQUIRED; error = LastLoadError; return false; }
                int previousState = Volatile.Read(ref loadState);
                nativeLoadPhase = 0; lastNativeException = null; trackedNativeBatch = false;
                bool accepted = action(out code, out error);
                if (nativeLoadPhase >= 3)
                {
                    // Native publication survives initializer failure. Report the
                    // actual visible set even when business initialization failed.
                    foreach (string name in (trackedNativeBatch ? FrozenAotSources.Keys : Enumerable.Empty<string>()).Concat(names.Select(NormalizeAssemblyName)))
                        LoadedAssemblies.Add(name);
                    for (int index = 0; index < names.Length; ++index)
                    {
                        string name = NormalizeAssemblyName(names[index]);
                        if (Artifacts.TryGetValue(name, out var artifact))
                        {
                            artifact.Current = (byte[])dlls[index].Clone();
                            if (IsDifferentialArtifact(artifact)) LoadedMutableAssemblies.Add(name);
                        }
                    }
                }
                if (!accepted && (nativeLoadPhase == 1 || nativeLoadPhase >= 3))
                {
                    code = nativeLoadPhase >= 3 ? LoadImageErrorCode.DHE_INITIALIZATION_FAILED : LoadImageErrorCode.DHE_RESTART_REQUIRED;
                    error = (nativeLoadPhase >= 3 ? "Current metadata is committed; initialization failed. " : "Native metadata preparation did not complete. ") +
                        "Restart the process before selecting a resource. " + (lastNativeException ?? error);
                    lastLoadError = error;
                    Volatile.Write(ref loadState, (int)DheLoadState.RestartRequired);
                    return false;
                }
                lastLoadError = accepted ? null : error;
                Volatile.Write(ref loadState, accepted ? (int)DheLoadState.Ready :
                    previousState == (int)DheLoadState.Ready ? previousState : (int)DheLoadState.Rejected);
                return accepted;
            }
            catch (Exception exception)
            {
                bool restart = Volatile.Read(ref nativeTouched) != 0;
                code = restart ? LoadImageErrorCode.DHE_RESTART_REQUIRED : LoadImageErrorCode.DHE_MV_REGISTRATION_FAILED;
                error = exception.ToString(); lastLoadError = error;
                Volatile.Write(ref loadState, (int)(restart ? DheLoadState.RestartRequired : DheLoadState.Rejected));
                return false;
            }
            // Release after every state/error/bookkeeping write. Reentrant module
            // calls never wait on a monitor held by their initiating thread.
            finally { Volatile.Write(ref loadBusy, 0); }
        }

        private static LoadImageErrorCode LoadTrackedNativeBatch(byte[][] dlls, byte[][] before, byte[][] after,
            uint[][] types, uint[][] methods, int[] kinds, uint[][] excluded, uint[][] conditional, byte[][] added)
        {
            try
            {
                trackedNativeBatch = true;
                return RuntimeApi.LoadDifferentialHybridAssemblyBatchWithPhase(dlls, before, after, types, methods,
                    kinds, excluded, conditional, added, out nativeLoadPhase);
            }
            catch (Exception exception) { lastNativeException = exception.ToString(); throw; }
            finally
            {
                if (nativeLoadPhase > 0) Volatile.Write(ref nativeTouched, 1);
                if (nativeLoadPhase >= 3) Volatile.Write(ref metadataCommitted, 1);
            }
        }

        public static bool LoadInterpreterAssemblyImage(string assemblyName, byte[] currentDll,
            out System.Reflection.Assembly assembly, out LoadImageErrorCode code, out string error)
        {
            System.Reflection.Assembly loaded = null;
            bool accepted = ExecuteLoad(new[] { assemblyName }, new[] { currentDll },
                (out LoadImageErrorCode result, out string message) =>
                    LoadInterpreterAssemblyImageCore(assemblyName, currentDll, out loaded, out result, out message), out code, out error);
            assembly = loaded;
            return accepted;
        }

        public static bool RunTransactionProbe(out string error) => ExecuteLoad(PlannedAssemblyNames,
            PlannedAssemblyNames.Select(name => Artifacts[name].Current).ToArray(),
            (out LoadImageErrorCode code, out string message) =>
            {
                bool accepted = RunTransactionProbeCore(out message);
                code = accepted ? LoadImageErrorCode.OK : transactionFailureCode;
                return accepted;
            }, out _, out error);
    }
}
