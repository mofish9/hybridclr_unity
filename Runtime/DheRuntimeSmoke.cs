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

    /// <summary>
    /// Project-independent DHE dispatch validation. Projects provide only the
    /// probe configuration and optional result comparison policy.
    /// </summary>
    public static class DheRuntimeSmoke
    {
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
    }
}
