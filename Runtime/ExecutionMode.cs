namespace HybridCLR
{
    /// <summary>One immutable choice for this native process. Persistence belongs to the project.</summary>
    public enum ExecutionMode
    {
        Unselected = 0,
        DifferentialHybrid = 1,
        Interpreter = 2,
    }

    public enum ExecutionModeSelectionResult
    {
        Success = 0,
        AlreadySelected = 1,
        InvalidMode = 2,
        NotSupported = 3,
    }
}
