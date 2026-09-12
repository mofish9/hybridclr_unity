namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static readonly string[] ToolOutputOptions =
    {
        "output", "binary", "outputroot", "identityoutput", "archiveroot", "assetroot", "stateroot"
    };

    private static void ProtectUnityToolOutputOptions(Cli cli)
    {
        foreach (string key in ToolOutputOptions)
        {
            string? value = cli.Optional(key);
            if (!string.IsNullOrWhiteSpace(value)) ProtectUnityToolOutput(value);
        }
    }

    // Protect the actually executing bundle, not an overridable CLI/schema root.
    // This is also called at shared output boundaries for config-derived paths
    // and internal calls which don't pass through Main again.
    private static void ProtectUnityToolOutput(string path)
    {
#if DHE_PACKAGE_TOOL
        string root = ResolveToolPhysicalPath(AppContext.BaseDirectory);
        string output = ResolveToolPhysicalPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (output.Equals(root, comparison) ||
            output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) ||
            root.StartsWith(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new DheException("Output must not overlap the executing tool bundle: " + path);
#endif
    }

    private static string ResolveToolPhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null)
                current = (info.ResolveLinkTarget(true) ?? throw new DheException("Cannot resolve output link: " + current)).FullName;
            else if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new DheException("Cannot validate output reparse point: " + current);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
