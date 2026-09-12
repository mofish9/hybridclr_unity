namespace HybridCLR.DheTool;

internal static class UnityBatchLog
{
    internal static string Read(string path)
    {
        // An Editor child can retain the writable log handle after Editor exit.
        // Keep validating the log without denying that existing writer access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
