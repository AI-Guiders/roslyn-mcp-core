namespace RoslynMcp.ServiceLayer;

/// <summary>
/// MSBuildWorkspace / MSBuild evaluation is process-global and not parallel-safe.
/// All OpenSolution/OpenProject paths must take this gate.
/// </summary>
internal static class MsBuildWorkspaceGate
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
