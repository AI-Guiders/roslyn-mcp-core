using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcp.ServiceLayer;

/// <summary>Структура solution: список проектов (имя, путь к .csproj). Только чтение, без загрузки компиляции.</summary>
public static class GetSolutionStructure
{
    private static async Task<Solution?> OpenSolutionOrProjectAsync(
        MSBuildWorkspace workspace,
        string solutionOrProjectPath,
        CancellationToken cancellationToken)
    {
        return await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);
    }

    private static StringBuilder BuildSolutionStructureText(string solutionOrProjectPath, Solution solution, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Solution structure");
        sb.AppendLineInvariant($"# Path: {solutionOrProjectPath}");
        sb.AppendLine("# Projects (name, path to .csproj) — use solution_or_project_path in other tools.");
        sb.AppendLine();

        AppendProjects(sb, solution, cancellationToken);

        sb.AppendLineInvariant($"Total: {solution.ProjectIds.Count} project(s).");
        return sb;
    }

    private static void AppendProjects(StringBuilder sb, Solution solution, CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = project.FilePath ?? "";
            sb.AppendLineInvariant($"{index}. {project.Name}");
            sb.AppendLineInvariant($"   {path}");
            sb.AppendLine();
            index++;
        }
    }

    public static async Task<string> GetStructureAsync(
        string solutionOrProjectPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";

        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);

            if (solution is null)
                return "Error: failed to open solution.";

            return BuildSolutionStructureText(solutionOrProjectPath, solution, cancellationToken).ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return "Error: .slnx format is not supported. Use .sln or .csproj.";
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }
}
