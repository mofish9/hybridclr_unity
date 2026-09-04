using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HybridCLR
{
    [Serializable]
    public sealed class DheSmokeConfig
    {
        public int schemaVersion;
        public DheSmokeProbe changedProbe;
        public DheSmokeProbe unchangedProbe;
        public DheSmokeProbe[] changedProbeCandidates;
        public DheSmokeProbe[] unchangedProbeCandidates;
    }

    [Serializable]
    public sealed class DheSmokeProbe
    {
        public string assemblyName;
        public string typeName;
        public string methodName;
        public bool hasIntegerArgument;
        public int integerArgument;
        public bool passNullArgument;
        public bool hasExpectedResult;
        public object expectedResult;
    }

    public sealed class DheDispatchProbeResult
    {
        public bool ChangedProbeChanged;
        public bool UnchangedProbeChanged;
        public DheSmokeProbe SelectedChangedProbe;
        public DheSmokeProbe SelectedUnchangedProbe;
        public int InterpreterEntryCount;
        public int AotBridgeCallCount;
        public int AotEntryCount;
    }

    public sealed class DhePlayerSmokeOptions
    {
        public bool StartupFinished;
        public string StartupError;
        public int ExpectedChangedMethodCount;
        public string ExpectedTarget;
        public string ExpectedNativeManifestSha256;
        public string ExpectedNativeGuardSourceSha256;
    }

    [Serializable]
    public sealed class DhePlayerSmokeResult
    {
        public int schemaVersion = 1;
        public string format = "hybridclr.dhe-player-result.json";
        public bool passed;
        public string error;
        public string loadError;
        public string[] plannedDheAssemblies;
        public string[] loadedDheAssemblies;
        public bool retryValidated;
        public string retryAssemblyName;
        public string retryFailure;
        public string transactionStatus;
        public DheAssemblySmokeValidation[] assemblyValidations;
        public bool multiAssemblyValidated;
        public bool buildIdentityValidated;
        public int identityVersion;
        public string aotSnapshotKind;
        public string nativeGuardSourceSha256;
        public string nativeManifestSha256;
        public string target;
        public bool dheEnabled;
        public int changedMethodCount;
        public int expectedChangedMethodCount;
        public int interpreterEntryCount;
        public int aotBridgeCallCount;
        public int aotEntryCount;
        public bool dispatchProbeValidated;
        public bool noOpAotBehaviorValidated;
        public bool changedProbeChanged;
        public bool unchangedProbeChanged;
        public DheSmokeProbeDescription selectedChangedProbe;
        public DheSmokeProbeDescription selectedUnchangedProbe;
        public string dispatchProbeError;
    }

    [Serializable]
    public sealed class DheAssemblySmokeValidation
    {
        public string assemblyName;
        public bool loaded;
        public bool hashValidated;
        public string loadError;
    }

    [Serializable]
    public sealed class DheSmokeProbeDescription
    {
        public string assemblyName;
        public string typeName;
        public string methodName;
    }

    /// <summary>
    /// Project-independent DHE dispatch validation. Projects provide only the
    /// probe configuration and optional result comparison policy.
    /// </summary>
    public static class DheRuntimeSmoke
    {
        public static DhePlayerSmokeResult EvaluatePlayer(DhePlayerSmokeOptions options,
            DheSmokeConfig config, Action<object, DheSmokeProbe> validateResult)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string startupError = options.StartupError;
            string[] planned = DheRuntime.PlannedAssemblyNames;
            string[] loaded = DheRuntime.LoadedAssemblyNames;
            var loadedSet = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);
            bool assemblySetValid = DheRuntime.Enabled && planned.Length == loaded.Length &&
                new HashSet<string>(planned, StringComparer.OrdinalIgnoreCase).SetEquals(loadedSet);
            string identityError = string.Empty;
            bool identityValid = DheRuntime.Enabled &&
                DheRuntime.ValidateEmbeddedIdentityForRuntime(options.ExpectedTarget,
                    options.ExpectedNativeManifestSha256,
                    options.ExpectedNativeGuardSourceSha256, out identityError);
            if (!identityValid && string.IsNullOrWhiteSpace(startupError))
                startupError = identityError;

            bool hasChangedMethods = options.ExpectedChangedMethodCount > 0;
            if (hasChangedMethods && options.StartupFinished && DheRuntime.Enabled)
            {
                try
                {
                    if (!DheRuntime.RunTransactionProbe(out string transactionError) &&
                        string.IsNullOrWhiteSpace(startupError))
                        startupError = transactionError;
                }
                catch (Exception exception)
                {
                    if (string.IsNullOrWhiteSpace(startupError))
                        startupError = exception.GetBaseException().Message;
                }
            }
            bool retryValidated = hasChangedMethods && assemblySetValid &&
                DheRuntime.TransactionRetryValidated;
            string transactionStatus = hasChangedMethods
                ? (retryValidated ? "validated" : "failed") : "notApplicable";

            bool dispatchValid = RunDispatchProbes(config, hasChangedMethods, validateResult,
                out DheDispatchProbeResult dispatch, out string dispatchError);
            bool noOpValid = !hasChangedMethods && options.StartupFinished && identityValid &&
                assemblySetValid && dispatchValid && !dispatch.ChangedProbeChanged &&
                !dispatch.UnchangedProbeChanged && dispatch.InterpreterEntryCount == 0;
            bool passed = options.StartupFinished && string.IsNullOrWhiteSpace(startupError) &&
                identityValid && assemblySetValid && (!hasChangedMethods || retryValidated) &&
                dispatchValid;
            string error = passed ? null : startupError ??
                (!DheRuntime.Enabled ? "DHE runtime plan was not enabled." :
                (!identityValid ? "DHE Player build identity did not match the requested build." :
                (!assemblySetValid ? "DHE planned/loaded assembly sets differ." :
                (hasChangedMethods && !retryValidated
                    ? "DHE transaction retry did not return registration failure." :
                (!dispatchValid ? dispatchError ??
                    "DHE changed/unchanged dispatch probe did not pass." :
                    "DHE dispatch probe failed.")))));

            var validations = new DheAssemblySmokeValidation[planned.Length];
            for (int index = 0; index < planned.Length; index++)
            {
                bool isLoaded = loadedSet.Contains(planned[index]);
                validations[index] = new DheAssemblySmokeValidation
                {
                    assemblyName = planned[index],
                    loaded = isLoaded,
                    hashValidated = identityValid && isLoaded,
                    loadError = identityValid && isLoaded ? "OK" :
                        "DHE_ASSEMBLY_VALIDATION_FAILED",
                };
            }
            return new DhePlayerSmokeResult
            {
                passed = passed,
                error = error,
                loadError = passed ? "OK" : error,
                plannedDheAssemblies = planned,
                loadedDheAssemblies = loaded,
                retryValidated = retryValidated,
                retryAssemblyName = DheRuntime.TransactionRetryAssemblyName,
                retryFailure = retryValidated ? DheRuntime.TransactionRetryFailure.ToString() : null,
                transactionStatus = transactionStatus,
                assemblyValidations = validations,
                multiAssemblyValidated = assemblySetValid,
                buildIdentityValidated = identityValid,
                identityVersion = DheRuntime.EmbeddedIdentityVersion,
                aotSnapshotKind = "managed-assembly-plus-generated-cpp-v1",
                nativeGuardSourceSha256 = options.ExpectedNativeGuardSourceSha256,
                nativeManifestSha256 = options.ExpectedNativeManifestSha256,
                target = options.ExpectedTarget,
                selectedPayloadVariantId = DheRuntime.SelectedPayloadVariantId,
                selectedPayloadCurrentAssemblySetSha256 =
                    DheRuntime.SelectedPayloadCurrentAssemblySetSha256,
                dheEnabled = DheRuntime.Enabled,
                changedMethodCount = options.ExpectedChangedMethodCount,
                expectedChangedMethodCount = options.ExpectedChangedMethodCount,
                interpreterEntryCount = dispatch.InterpreterEntryCount,
                aotBridgeCallCount = dispatch.AotBridgeCallCount,
                aotEntryCount = dispatch.AotEntryCount,
                dispatchProbeValidated = dispatchValid,
                noOpAotBehaviorValidated = noOpValid,
                changedProbeChanged = dispatch.ChangedProbeChanged,
                unchangedProbeChanged = dispatch.UnchangedProbeChanged,
                selectedChangedProbe = DescribeProbe(dispatch.SelectedChangedProbe),
                selectedUnchangedProbe = DescribeProbe(dispatch.SelectedUnchangedProbe),
                dispatchProbeError = dispatchError,
            };
        }

        public static bool RunDispatchProbes(DheSmokeConfig config, bool requireChanged,
            Action<object, DheSmokeProbe> validateResult, out DheDispatchProbeResult result,
            out string error)
        {
            result = new DheDispatchProbeResult();
            error = string.Empty;
            try
            {
                if (config == null || config.schemaVersion != 1 || config.changedProbe == null ||
                    config.unchangedProbe == null)
                    throw new InvalidDataException(
                        "DHE smoke configuration has an invalid schema or probes.");

                if (!TrySelectProbe(config.changedProbe, config.changedProbeCandidates,
                        requireChanged, out DheSmokeProbe changedProbe,
                        out MethodInfo changedMethod, out bool changed, out error) ||
                    !TrySelectProbe(config.unchangedProbe, config.unchangedProbeCandidates,
                        false, out DheSmokeProbe unchangedProbe, out MethodInfo unchangedMethod,
                        out bool unchanged, out error))
                    return false;

                result.SelectedChangedProbe = changedProbe;
                result.SelectedUnchangedProbe = unchangedProbe;
                result.ChangedProbeChanged = changed;
                result.UnchangedProbeChanged = unchanged;
                var planned = new HashSet<string>(DheRuntime.PlannedAssemblyNames,
                    StringComparer.OrdinalIgnoreCase);
                if (!planned.Contains(NormalizeAssemblyName(changedProbe.assemblyName)) ||
                    !planned.Contains(NormalizeAssemblyName(unchangedProbe.assemblyName)))
                {
                    error = "DHE dispatch probes must target assemblies in the runtime plan.";
                    return false;
                }

                RuntimeApi.ResetDifferentialDispatchCounters();
                int interpreterBefore = RuntimeApi.GetDifferentialInterpreterEntryCount();
                object changedResult = InvokeProbe(changedMethod, changedProbe);
                ValidateResult(changedResult, changedProbe, validateResult);
                int interpreterAfterChanged = RuntimeApi.GetDifferentialInterpreterEntryCount();
                object unchangedResult = InvokeProbe(unchangedMethod, unchangedProbe);
                ValidateResult(unchangedResult, unchangedProbe, validateResult);
                int interpreterAfterUnchanged = RuntimeApi.GetDifferentialInterpreterEntryCount();
                result.InterpreterEntryCount = interpreterAfterUnchanged;
                result.AotBridgeCallCount = RuntimeApi.GetDifferentialAotBridgeCallCount();
                result.AotEntryCount = RuntimeApi.GetDifferentialAotEntryCount();

                if (unchanged)
                {
                    error = "Selected unchanged probe is incorrectly marked as a differential method.";
                    return false;
                }
                if (requireChanged && interpreterAfterChanged <= interpreterBefore)
                {
                    error = "Selected changed probe did not enter the interpreter.";
                    return false;
                }
                if (interpreterAfterUnchanged != interpreterAfterChanged)
                {
                    error = "Selected unchanged probe unexpectedly entered the interpreter.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        private static bool TrySelectProbe(DheSmokeProbe primary, DheSmokeProbe[] candidates,
            bool requireChanged, out DheSmokeProbe selected, out MethodInfo method,
            out bool isChanged, out string error)
        {
            selected = null;
            method = null;
            isChanged = false;
            error = string.Empty;
            IEnumerable<DheSmokeProbe> probes = new[] { primary }.Concat(
                candidates ?? Array.Empty<DheSmokeProbe>()).Where(item => item != null);
            string lastError = null;
            foreach (DheSmokeProbe probe in probes)
            {
                try
                {
                    MethodInfo candidate = ResolveProbeMethod(probe);
                    bool changed = RuntimeApi.IsDifferentialMethodChanged(candidate);
                    if (changed == requireChanged)
                    {
                        selected = probe;
                        method = candidate;
                        isChanged = changed;
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    lastError = exception.GetBaseException().Message;
                }
            }
            error = requireChanged
                ? "No configured changed probe is marked as a differential method."
                : "No configured unchanged probe is available.";
            if (!string.IsNullOrWhiteSpace(lastError)) error += " Last probe error: " + lastError;
            return false;
        }

        private static MethodInfo ResolveProbeMethod(DheSmokeProbe probe)
        {
            if (string.IsNullOrWhiteSpace(probe.assemblyName) ||
                string.IsNullOrWhiteSpace(probe.typeName) ||
                string.IsNullOrWhiteSpace(probe.methodName))
                throw new InvalidDataException("DHE smoke probe is incomplete.");
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, NormalizeAssemblyName(probe.assemblyName),
                    StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
                throw new InvalidDataException(
                    "DHE smoke probe assembly is not loaded: " + probe.assemblyName);
            Type type = assembly.GetType(probe.typeName, false);
            if (type == null)
                throw new InvalidDataException("DHE smoke probe type was not found: " + probe.typeName);
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance)
                .Where(candidate => candidate.Name == probe.methodName &&
                    !candidate.IsGenericMethodDefinition && ProbeParametersMatch(candidate, probe))
                .ToArray();
            if (methods.Length != 1)
                throw new InvalidDataException("DHE smoke probe must resolve to exactly one method: " +
                    probe.typeName + "." + probe.methodName);
            return methods[0];
        }

        private static bool ProbeParametersMatch(MethodInfo method, DheSmokeProbe probe)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (probe.passNullArgument)
                return parameters.Length == 1 && !parameters[0].ParameterType.IsValueType;
            if (probe.hasIntegerArgument)
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
            return parameters.Length == 0;
        }

        private static object InvokeProbe(MethodInfo method, DheSmokeProbe probe)
        {
            object[] arguments = probe.passNullArgument ? new object[] { null } :
                probe.hasIntegerArgument ? new object[] { probe.integerArgument } : null;
            object target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType);
            return method.Invoke(target, arguments);
        }

        private static void ValidateResult(object actual, DheSmokeProbe probe,
            Action<object, DheSmokeProbe> validateResult)
        {
            if (!probe.hasExpectedResult) return;
            if (validateResult == null)
                throw new InvalidDataException(
                    "DHE smoke probe expects a result but no comparison policy was supplied.");
            validateResult(actual, probe);
        }

        private static string NormalizeAssemblyName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? string.Empty :
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(name) : name.Trim());
        }

        private static DheSmokeProbeDescription DescribeProbe(DheSmokeProbe probe)
        {
            return probe == null ? null : new DheSmokeProbeDescription
            {
                assemblyName = probe.assemblyName,
                typeName = probe.typeName,
                methodName = probe.methodName,
            };
        }
    }
}
