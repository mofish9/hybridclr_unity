using System.Text.Json;

namespace HybridCLR.DheTool;

internal static class ToolchainSourceCoverage
{
    // The host project compiles all top-level tool/*.cs. Its distribution
    // must carry that same source set; a self-consistent file inventory alone
    // cannot detect a source file omitted from both layout and manifest.
    internal static void Validate(string root, JsonElement layout)
    {
        var exact = layout.GetProperty("exactPaths").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var prefixes = layout.GetProperty("prefixes").EnumerateArray()
            .Select(item => item.GetString()!.TrimEnd('/') + "/").ToArray();
        var required = Directory.GetFiles(Path.Combine(root, "tool"), "*.cs")
            .Select(path => "tool/" + Path.GetFileName(path))
            .Concat(new[] { "tool/HybridCLR.DheTool.csproj", "tool/dnlib.dll",
                "Directory.Build.props", "Directory.Build.targets",
                "templates/DheWorkflowBuild.cs", "templates/DheBuildIdentity.cs" });
        foreach (string path in required.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(root, path)))
                throw new InvalidDataException("Required toolchain source is missing: " + path);
            if (!exact.Contains(path) && !prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidDataException("Toolchain layout omits required build input: " + path);
        }
    }
}
