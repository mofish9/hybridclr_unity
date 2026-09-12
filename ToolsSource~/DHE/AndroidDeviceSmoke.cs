using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private sealed record AdbDevice(string Serial, string State, string Description);
    private sealed record AdbCommandResult(int ExitCode, string StandardOutput,
        string StandardError);
    private sealed record AndroidStagedFile(string RelativePath, string LocalPath,
        string Sha256);

    private static int AndroidDeviceSmoke(Cli cli)
    {
        string adbPath = RequireFile(cli.Require("adbpath"), "Android Debug Bridge");
        string apkPath = RequireFile(cli.Require("apk"), "Android Base APK");
        string resourceRoot = RequireDirectory(cli.Require("resourceupdateroot"),
            "DHE resource update root");
        string stagePath = RequireFile(cli.Require("stagereport"),
            "DHE Android resource stage report");
        string baseBuildIdentityPath = RequireFile(cli.Require("basebuildidentity"),
            "Android Base build identity");
        string applicationId = ValidateAndroidApplicationId(cli.Require("applicationid"));
        string activity = ValidateAndroidActivity(cli.Optional("activity") ??
            "com.unity3d.player.UnityPlayerActivity");
        string outputRoot = SafeOutputRoot(cli.Require("outputroot"), new[]
        {
            adbPath, apkPath, resourceRoot, stagePath, baseBuildIdentityPath,
        });
        foreach (string protectedRoot in new[]
                 {
                     resourceRoot, Path.GetDirectoryName(stagePath)!,
                     Path.GetDirectoryName(baseBuildIdentityPath)!,
                     Path.GetDirectoryName(apkPath)!,
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
            EnsureOutputOutsideRoot(outputRoot, protectedRoot);
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new DheException("Android device smoke OutputRoot must be empty or absent.");
        Directory.CreateDirectory(outputRoot);

        int timeoutSeconds = ReadAndroidTimeout(cli.Optional("timeoutseconds"));
        JsonElement stage = ReadJson<JsonElement>(stagePath);
        RequireEvidenceFormat(stage, "hybridclr.dhe-resource-stage.json",
            "Android resource stage");
        JsonElement identity = ReadJson<JsonElement>(baseBuildIdentityPath);
        RequireEvidenceFormat(identity, "hybridclr.dhe-build-identity.json",
            "Android Base build identity");
        if (!GetBool(stage, "passed") || !GetBool(stage, "releaseReady") ||
            !string.Equals(GetString(stage, "embeddedBaseSourceKind"), "android-apk",
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(GetString(stage,
                    "embeddedBaseArtifactPath") ?? string.Empty), apkPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "embeddedBaseArtifactSha256"),
                Sha256File(apkPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(GetString(stage,
                    "baseBuildIdentityPath") ?? string.Empty), baseBuildIdentityPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "baseBuildIdentitySha256"),
                Sha256File(baseBuildIdentityPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(identity, "target"), "Android",
                StringComparison.Ordinal) ||
            !string.Equals(GetString(identity, "baseId"),
                GetString(stage, "selectedBaseId"), StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Android APK, Base identity, and resource stage are not bound together.");

        string manifestPath = RequireFile(Path.Combine(resourceRoot,
            "dhe-resource-update.json"), "DHE resource update manifest");
        JsonElement manifest = ReadJson<JsonElement>(manifestPath);
        RequireEvidenceFormat(manifest, "hybridclr.dhe-resource-update.json",
            "DHE resource update manifest");
        ReleaseLedgerDocument? ledger = ValidateReleaseLedgerForStaging(resourceRoot,
            manifest);
        _ = ValidateResourceUpdateCompatibility(resourceRoot, manifest);
        if (ledger == null ||
            !string.Equals(Path.GetFullPath(GetString(stage, "updateRoot") ?? string.Empty),
                resourceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(stage, "releaseLedgerSha256"), ledger.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Android device smoke requires a matching Release resource ledger.");

        string assetRoot = RequireDirectory(GetString(stage, "assetRoot") ?? string.Empty,
            "Staged Android external asset root");
        AndroidStagedFile[] stagedFiles = ReadAndroidStagedFiles(stage, assetRoot);
        string apkSha256 = Sha256File(apkPath);
        JsonElement[] immutableApkRecords = stage.GetProperty("immutableFiles")
            .EnumerateArray().Where(item => string.Equals(
                Path.GetFullPath(GetString(item, "path") ?? string.Empty), apkPath,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (immutableApkRecords.Length != 1 ||
            !string.Equals(GetString(immutableApkRecords[0], "sha256Before"), apkSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(immutableApkRecords[0], "sha256After"), apkSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new DheException("Android Base APK is not an immutable staged input.");

        AdbCommandResult versionResult = RunAdb(adbPath, null,
            new[] { "version" }, timeoutSeconds);
        RequireAdbSuccess(versionResult, "read adb version");
        AdbCommandResult devicesResult = RunAdb(adbPath, null,
            new[] { "devices", "-l" }, timeoutSeconds);
        RequireAdbSuccess(devicesResult, "enumerate Android devices");
        AdbDevice device = SelectAdbDevice(ParseAdbDevices(devicesResult.StandardOutput),
            cli.Optional("deviceserial"));
        string serial = device.Serial;

        string deviceModel = ReadAndroidProperty(adbPath, serial, "ro.product.model",
            timeoutSeconds);
        string deviceProduct = ReadAndroidProperty(adbPath, serial, "ro.product.name",
            timeoutSeconds);
        string deviceApi = ReadAndroidProperty(adbPath, serial, "ro.build.version.sdk",
            timeoutSeconds);
        string deviceAbi = ReadAndroidProperty(adbPath, serial, "ro.product.cpu.abi",
            timeoutSeconds);
        if (!int.TryParse(deviceApi, out int apiLevel) || apiLevel < 21 ||
            string.IsNullOrWhiteSpace(deviceAbi))
            throw new DheException("Android device API/ABI identity is invalid.");

        string filesRoot = "/sdcard/Android/data/" + applicationId + "/files";
        string remoteAssetRoot = ValidateAndroidRemoteAssetRoot(
            cli.Optional("remoteassetroot") ?? filesRoot + "/HybridCLRLab/DheUpdate",
            applicationId);
        string remoteResult = filesRoot + "/hybridclr-lab-dhe-result.json";
        string component = activity.StartsWith(".", StringComparison.Ordinal)
            ? applicationId + "/" + activity
            : applicationId + "/" + activity;
        var startedAt = Stopwatch.StartNew();

        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "install", "-r", "-d", "-g", apkPath }, timeoutSeconds),
            "install Android Base APK");
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "pm", "clear", applicationId }, timeoutSeconds),
            "clear Android application data");
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "rm", "-rf", remoteAssetRoot }, timeoutSeconds),
            "clear the scoped Android DHE asset root");
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "mkdir", "-p", remoteAssetRoot }, timeoutSeconds),
            "create the Android DHE asset root");

        string roundTripRoot = Path.Combine(Path.GetTempPath(),
            "hybridclr-dhe-adb-pull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roundTripRoot);
        try
        {
            foreach (AndroidStagedFile file in stagedFiles)
            {
                string remotePath = remoteAssetRoot + "/" + file.RelativePath;
                string remoteDirectory = remotePath[..remotePath.LastIndexOf('/')];
                RequireAdbSuccess(RunAdb(adbPath, serial,
                    new[] { "shell", "mkdir", "-p", remoteDirectory }, timeoutSeconds),
                    "create Android staged asset directory");
                RequireAdbSuccess(RunAdb(adbPath, serial,
                    new[] { "push", file.LocalPath, remotePath }, timeoutSeconds),
                    "push Android staged asset " + file.RelativePath);
                string roundTrip = Path.Combine(roundTripRoot,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(roundTrip)!);
                RequireAdbSuccess(RunAdb(adbPath, serial,
                    new[] { "pull", remotePath, roundTrip }, timeoutSeconds),
                    "read back Android staged asset " + file.RelativePath);
                if (!string.Equals(Sha256File(roundTrip), file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DheException(
                        "Android staged asset changed during device transfer: " +
                        file.RelativePath);
            }
        }
        finally
        {
            if (Directory.Exists(roundTripRoot)) Directory.Delete(roundTripRoot, true);
        }

        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "rm", "-f", remoteResult }, timeoutSeconds),
            "clear the previous Android Player result");
        _ = RunAdb(adbPath, serial, new[] { "logcat", "-c" }, timeoutSeconds);
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "am", "force-stop", applicationId }, timeoutSeconds),
            "stop the previous Android Player");
        string unityArguments = "-labMode dhe -labTarget Android " +
            "-labAotMetadataMode supplemental -labDheAssetRoot " + remoteAssetRoot +
            " -labResult " + remoteResult + " -labHoldSeconds 3";
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "shell", "am", "start", "-W", "-n", component,
                "--es", "unity", unityArguments }, timeoutSeconds),
            "launch the Android DHE Player");

        int processId = WaitForUniqueAndroidProcess(adbPath, serial, applicationId,
            remoteResult, timeoutSeconds);
        WaitForAndroidFile(adbPath, serial, remoteResult, timeoutSeconds);
        string playerResultPath = Path.Combine(outputRoot, "dhe-player-result.json");
        RequireAdbSuccess(RunAdb(adbPath, serial,
            new[] { "pull", remoteResult, playerResultPath }, timeoutSeconds),
            "pull the Android DHE Player result");
        JsonElement player = ReadJson<JsonElement>(playerResultPath);
        RequireEvidenceFormat(player, "hybridclr.dhe-player-result.json",
            "Android DHE Player result");
        if (!GetBool(player, "passed") ||
            !string.Equals(GetString(player, "target"), "Android",
                StringComparison.Ordinal) ||
            !string.Equals(GetString(player, "selectedBaseId"),
                GetString(stage, "selectedBaseId"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(player, "selectedPayloadVariantId") ?? "default",
                GetString(stage, "payloadVariantId") ?? "default",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(player,
                    "selectedPayloadCurrentAssemblySetSha256"),
                GetString(stage, "currentAssemblySetSha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new DheException(
                "Android Player result does not match the staged Base and payload.");

        string logcatPath = Path.Combine(outputRoot, "adb-logcat.txt");
        AdbCommandResult logcat = RunAdb(adbPath, serial,
            new[] { "logcat", "-d", "-v", "threadtime" }, timeoutSeconds);
        File.WriteAllText(logcatPath,
            logcat.StandardOutput + logcat.StandardError, new UTF8Encoding(false));
        bool processExited = WaitForAndroidProcessExit(adbPath, serial, applicationId, 15);
        if (!processExited)
            throw new DheException("Android DHE Player did not exit after writing its result.");

        startedAt.Stop();
        string schemaRoot = RequireDirectory(cli.Optional("schemasroot") ??
            Path.Combine(cli.Root, "schemas"), "DHE schema root");
        var report = new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-device-player-run.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = true,
            target = "Android",
            deviceSerial = serial,
            deviceState = device.State,
            deviceDescription = device.Description,
            deviceModel,
            deviceProduct,
            deviceApiLevel = apiLevel,
            deviceAbi,
            adbPath,
            adbVersion = FirstNonEmptyLine(versionResult.StandardOutput),
            apkPath,
            apkSha256,
            applicationId,
            activity,
            processId,
            uniqueProcessId = true,
            processExited,
            baseBuildIdentity = baseBuildIdentityPath,
            baseBuildIdentitySha256 = Sha256File(baseBuildIdentityPath),
            selectedBaseId = GetString(stage, "selectedBaseId"),
            resourceUpdateRoot = resourceRoot,
            resourceUpdateManifest = manifestPath,
            resourceUpdateManifestSha256 = Sha256File(manifestPath),
            releaseLedger = ledger.SourcePath,
            releaseLedgerSha256 = ledger.Sha256,
            stageReport = stagePath,
            stageReportSha256 = Sha256File(stagePath),
            stagedAssetRoot = assetRoot,
            stagedFileCount = stagedFiles.Length,
            stagedFiles = stagedFiles.Select(file => new
            {
                path = file.RelativePath,
                sha256 = file.Sha256,
            }).ToArray(),
            remoteAssetRoot,
            playerResult = playerResultPath,
            playerResultSha256 = Sha256File(playerResultPath),
            playerPassed = true,
            selectedPayloadVariantId = GetString(stage, "payloadVariantId") ?? "default",
            selectedCurrentAssemblySetSha256 = GetString(stage,
                "currentAssemblySetSha256"),
            logcat = logcatPath,
            logcatSha256 = Sha256File(logcatPath),
            installValidated = true,
            payloadPushValidated = true,
            payloadRoundTripValidated = true,
            launchValidated = true,
            elapsedMilliseconds = startedAt.ElapsedMilliseconds,
            errors = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
        };
        JsonElement reportElement = JsonSerializer.SerializeToElement(report, Json);
        ValidateDocumentAgainstSchema(reportElement, schemaRoot,
            "dhe-device-player-run.schema.json", "Android device Player run");
        string output = Path.Combine(outputRoot, "dhe-device-player-run.json");
        WriteJson(output, report);
        Console.WriteLine("DHE Android device smoke passed: " + output);
        return 0;
    }

    private static AndroidStagedFile[] ReadAndroidStagedFiles(JsonElement stage,
        string assetRoot)
    {
        var files = new Dictionary<string, AndroidStagedFile>(
            StringComparer.OrdinalIgnoreCase);
        void Add(string path, string? expectedHash = null)
        {
            string local = RequireFile(path, "Staged Android DHE asset");
            string relative = Path.GetRelativePath(assetRoot, local)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!IsPortableRelativePath(relative))
                throw new DheException(
                    "Staged Android DHE asset is outside AssetRoot: " + local);
            string sha256 = Sha256File(local);
            if (expectedHash != null && !string.Equals(expectedHash, sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new DheException("Staged Android DHE asset hash changed: " + relative);
            if (!files.TryAdd(relative, new AndroidStagedFile(relative, local, sha256)))
                throw new DheException("Duplicate staged Android DHE asset: " + relative);
        }

        foreach (JsonElement file in stage.GetProperty("stagedFiles").EnumerateArray())
        {
            string relative = GetString(file, "path") ?? string.Empty;
            Add(ResolveContainedPath(assetRoot, relative, "Staged Android payload"),
                GetString(file, "sha256"));
        }
        Add(GetString(stage, "stagedPlanPath") ?? string.Empty,
            GetString(stage, "stagedPlanSha256"));
        Add(GetString(stage, "stagedManifestPath") ?? string.Empty);
        Add(GetString(stage, "stagedValidationPath") ?? string.Empty);
        Add(GetString(stage, "stagedReleaseLedgerPath") ?? string.Empty,
            GetString(stage, "releaseLedgerSha256"));
        return files.Values.OrderBy(file => file.RelativePath,
            StringComparer.Ordinal).ToArray();
    }

    private static string ValidateAndroidApplicationId(string value)
    {
        string result = value.Trim();
        if (!Regex.IsMatch(result,
                "^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+$"))
            throw new DheException("Android ApplicationId is invalid: " + value);
        return result;
    }

    private static string ValidateAndroidActivity(string value)
    {
        string result = value.Trim();
        if (!Regex.IsMatch(result,
                "^(\\.[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)+)$"))
            throw new DheException("Android Activity is invalid: " + value);
        return result;
    }

    private static string ValidateAndroidRemoteAssetRoot(string value,
        string applicationId)
    {
        string result = value.Replace('\\', '/').TrimEnd('/');
        string prefix = "/sdcard/Android/data/" + applicationId + "/files/";
        if (!result.StartsWith(prefix, StringComparison.Ordinal) ||
            result.Split('/').Any(segment => segment is "." or "..") ||
            result.Length <= prefix.Length)
            throw new DheException(
                "RemoteAssetRoot must stay below the selected application's external files root.");
        return result;
    }

    private static int ReadAndroidTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 180;
        if (!int.TryParse(value, out int result) || result < 10 || result > 1800)
            throw new DheException("Android device timeout must be between 10 and 1800 seconds.");
        return result;
    }

    private static AdbDevice[] ParseAdbDevices(string output)
    {
        return output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 &&
                !line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith('*'))
            .Select(line =>
            {
                string[] fields = line.Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2)
                    throw new DheException("adb returned an invalid device row: " + line);
                return new AdbDevice(fields[0], fields[1],
                    string.Join(' ', fields.Skip(2)));
            }).ToArray();
    }

    private static AdbDevice SelectAdbDevice(IEnumerable<AdbDevice> devices,
        string? requestedSerial)
    {
        AdbDevice[] all = devices.ToArray();
        if (!string.IsNullOrWhiteSpace(requestedSerial))
        {
            AdbDevice[] selected = all.Where(device => string.Equals(device.Serial,
                requestedSerial.Trim(), StringComparison.Ordinal)).ToArray();
            if (selected.Length != 1 || selected[0].State != "device")
                throw new DheException(
                    "The requested Android device is absent, offline, or unauthorized.");
            return selected[0];
        }
        AdbDevice[] ready = all.Where(device => device.State == "device").ToArray();
        if (ready.Length != 1)
            throw new DheException(
                "Android device smoke requires exactly one ready device or -DeviceSerial; found " +
                ready.Length + ".");
        return ready[0];
    }

    private static string ReadAndroidProperty(string adbPath, string serial,
        string property, int timeoutSeconds)
    {
        AdbCommandResult result = RunAdb(adbPath, serial,
            new[] { "shell", "getprop", property }, timeoutSeconds);
        RequireAdbSuccess(result, "read Android property " + property);
        string value = FirstNonEmptyLine(result.StandardOutput);
        if (string.IsNullOrWhiteSpace(value))
            throw new DheException("Android property is empty: " + property);
        return value;
    }

    private static int WaitForUniqueAndroidProcess(string adbPath, string serial,
        string applicationId, string remoteResult, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Min(timeoutSeconds, 15));
        while (DateTime.UtcNow < deadline)
        {
            AdbCommandResult result = RunAdb(adbPath, serial,
                new[] { "shell", "pidof", applicationId }, 10);
            int[] processIds = result.StandardOutput.Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out int processId) ? processId : 0)
                .Where(processId => processId > 0).Distinct().ToArray();
            if (processIds.Length == 1) return processIds[0];
            if (processIds.Length > 1)
                throw new DheException("Android Player has more than one process ID.");
            Thread.Sleep(200);
        }
        throw new DheException(
            "Android Player did not expose one process before the smoke timeout: " +
            remoteResult);
    }

    private static void WaitForAndroidFile(string adbPath, string serial,
        string remotePath, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            AdbCommandResult result = RunAdb(adbPath, serial,
                new[] { "shell", "ls", remotePath }, 10);
            if (result.ExitCode == 0 && result.StandardOutput.Trim() == remotePath)
                return;
            Thread.Sleep(250);
        }
        throw new DheException("Android Player result was not written before timeout.");
    }

    private static bool WaitForAndroidProcessExit(string adbPath, string serial,
        string applicationId, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            AdbCommandResult result = RunAdb(adbPath, serial,
                new[] { "shell", "pidof", applicationId }, 10);
            if (string.IsNullOrWhiteSpace(result.StandardOutput)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static AdbCommandResult RunAdb(string adbPath, string? serial,
        IEnumerable<string> arguments, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(adbPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(serial))
        {
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(serial);
        }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new DheException("Unable to start adb.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(checked(timeoutSeconds * 1000)))
        {
            try { process.Kill(true); } catch { }
            throw new DheException("adb timed out after " + timeoutSeconds + " seconds.");
        }
        return new AdbCommandResult(process.ExitCode,
            stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void RequireAdbSuccess(AdbCommandResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new DheException("Unable to " + operation + ": " +
                (result.StandardError + " " + result.StandardOutput).Trim());
    }

    private static string FirstNonEmptyLine(string value) =>
        value.Replace("\r", string.Empty).Split('\n')
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
}
