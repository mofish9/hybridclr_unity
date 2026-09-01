using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Runs the standard headless DHE Player smoke protocol. Device platforms
    /// are recorded as external validation gates unless the project supplies a
    /// local runner for them.
    /// </summary>
    public static class DheProjectSmokeSupport
    {
        public static void Run(DheProjectPlayerSmokeContext context,
            DheProjectPlayerSmokeOptions options = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            options = options ?? new DheProjectPlayerSmokeOptions();
            string outputRoot = Path.GetFullPath(context.OutputRoot ?? string.Empty);
            string adapterRoot = Path.Combine(outputRoot, "adapter");
            Directory.CreateDirectory(adapterRoot);
            bool canRun = options.CanRunTarget?.Invoke(context.Target) ??
                (context.Target == BuildTarget.StandaloneWindows64 &&
                 Application.platform == RuntimePlatform.WindowsEditor);
            if (!canRun)
            {
                WriteJson(Path.Combine(adapterRoot, "player-smoke-gate.json"),
                    new PlayerSmokeGate
                    {
                        schemaVersion = 1,
                        format = "hybridclr.dhe-player-smoke-gate.json",
                        passed = false,
                        target = context.TargetName,
                        status = "external-platform-validation-required",
                        playerOutput = Path.GetFullPath(context.PlayerPath),
                    });
                UnityEngine.Debug.LogWarning("DHE Player smoke requires the target platform lane: " +
                    context.TargetName);
                return;
            }

            string executable = RequireFile(context.PlayerPath, "Player executable");
            string resultPath = Path.Combine(outputRoot, "dhe-player-result.json");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            DheNativeGuardResult guard = context.NativeResult?.GuardResult ??
                throw new BuildFailedException("DHE Player smoke has no native guard result.");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "-batchmode", "-nographics",
                "-dheSmokeReport", resultPath,
                "-dheExpectedChanged", guard.RequestedMethodCount.ToString(),
                "-dheTarget", context.TargetName,
                "-dheNativeManifestHash", guard.NativeManifestSha256,
                "-dheNativeGuardHash", guard.NativeGuardSourceSha256,
                "-logFile", Path.Combine(outputRoot, "dhe-player.log"),
            })
                start.ArgumentList.Add(argument);

            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new BuildFailedException(
                        "Unable to start the DHE Player smoke process.");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(options.TimeoutMilliseconds);
                if (!exited)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                    catch
                    {
                    }
                }
                Task.WaitAll(stdout, stderr);
                File.WriteAllText(Path.Combine(outputRoot, "dhe-player-process.log"),
                    stdout.Result + Environment.NewLine + stderr.Result,
                    new UTF8Encoding(false));
                if (!exited)
                    throw new TimeoutException("DHE Player smoke timed out after " +
                        options.TimeoutMilliseconds + " milliseconds.");
                if (process.ExitCode != 0)
                    throw new BuildFailedException(
                        "DHE Player smoke exited with code " + process.ExitCode + ".");
            }

            string reportPath = RequireFile(resultPath, "Player smoke report");
            PlayerSmokeResult report = JsonUtility.FromJson<PlayerSmokeResult>(
                File.ReadAllText(reportPath));
            if (report == null || !report.passed)
                throw new BuildFailedException(
                    "DHE Player smoke reported failure: " + reportPath);
        }

        private static string RequireFile(string path, string description)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            if (!File.Exists(full))
                throw new FileNotFoundException("DHE " + description + " was not found", full);
            return full;
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        }

        [Serializable]
        private sealed class PlayerSmokeGate
        {
            public int schemaVersion;
            public string format;
            public bool passed;
            public string target;
            public string status;
            public string playerOutput;
        }

        [Serializable]
        private sealed class PlayerSmokeResult
        {
            public bool passed;
        }
    }

    public sealed class DheProjectPlayerSmokeOptions
    {
        public int TimeoutMilliseconds = 660000;
        public Func<BuildTarget, bool> CanRunTarget;
    }
}
