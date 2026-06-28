using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcp.ServiceLayer;

internal static class WorkspaceOpen
{
    public static async Task<Solution?> OpenSolutionOrProjectAsync(
        MSBuildWorkspace workspace,
        string solutionOrProjectPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetExtension(solutionOrProjectPath), ".sln", StringComparison.OrdinalIgnoreCase))
            return await workspace.OpenSolutionAsync(solutionOrProjectPath, cancellationToken: cancellationToken).ConfigureAwait(false);

        var project = await workspace.OpenProjectAsync(solutionOrProjectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return project.Solution;
    }
}

