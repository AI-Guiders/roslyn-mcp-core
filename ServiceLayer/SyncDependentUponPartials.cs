using System.Text;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Массовая простановка <c>DependentUpon</c> для partial-файлов вида <c>Type.Part.cs</c> под <c>Type.cs</c> в той же папке
/// (эвристика как в CascadeIDE: самый длинный существующий префикс <c>Stem.cs</c>).
/// </summary>
public static class SyncDependentUponPartials
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <summary>
    /// Для всех подходящих .cs в проекте(ах) добавляет <c>Compile Update</c> с DependentUpon. <paramref name="dryRun"/> — только отчёт без записи .csproj.
    /// </summary>
    public static async Task<string> SyncAsync(
        string solutionOrProjectPath,
        string? projectPathFilter,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";

        var filterNorm = string.IsNullOrWhiteSpace(projectPathFilter) ? null : NormalizePath(projectPathFilter);

        MSBuildWorkspace? workspace = null;
        try
        {
            workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            var solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);
            if (solution is null)
                return "Error: failed to open solution.";

            var sb = new StringBuilder();
            sb.AppendLineInvariant($"# DependentUpon sync (dry_run={dryRun})");
            sb.AppendLine();

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var csproj = project.FilePath;
                if (string.IsNullOrEmpty(csproj) || !File.Exists(csproj))
                    continue;
                if (filterNorm is not null && !string.Equals(NormalizePath(csproj), filterNorm, StringComparison.OrdinalIgnoreCase))
                    continue;

                var projDir = Path.GetDirectoryName(csproj);
                if (string.IsNullOrEmpty(projDir))
                    continue;

                sb.AppendLineInvariant($"## Project: {csproj}");
                ProcessProject(csproj, projDir, dryRun, sb, cancellationToken);
                sb.AppendLine();
            }

            if (dryRun)
                sb.AppendLine("# Re-run with dry_run: false to write .csproj files.");
            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return "Error: .slnx format is not supported. Use .sln or .csproj.";
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private static void ProcessProject(
        string csprojPath,
        string projectDirectory,
        bool dryRun,
        StringBuilder log,
        CancellationToken ct)
    {
        var projDirFull = Path.GetFullPath(projectDirectory);

        var csFiles = new List<string>();
        foreach (var f in Directory.EnumerateFiles(projDirFull, "*.cs", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(projDirFull, f);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            csFiles.Add(Path.GetFullPath(f));
        }

        var pairs = new List<(string ChildRel, string DepVal)>();
        var byDir = csFiles.GroupBy(f => Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var group in byDir)
        {
            ct.ThrowIfCancellationRequested();
            var names = new HashSet<string>(
                group.Select(g => Path.GetFileName(g)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var childFull in group)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(childFull);
                if (fileName is null)
                    continue;
                var parentName = DependentUponCsproj.TryInferParentCsFileName(fileName, names);
                if (parentName is null)
                    continue;
                var parentFull = Path.GetFullPath(Path.Combine(group.Key, parentName));
                if (!File.Exists(parentFull) || string.Equals(childFull, parentFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                var childRel = Path.GetRelativePath(projDirFull, childFull).Replace('/', Path.DirectorySeparatorChar);
                var depVal = DependentUponCsproj.ComputeDependentUponValue(projDirFull, childFull, parentFull);
                pairs.Add((childRel, depVal));
            }
        }

        if (pairs.Count == 0)
        {
            log.AppendLine("  (no Stem.Rest.cs pairs found under project tree)");
            return;
        }

        var deduped = pairs
            .GroupBy(p => p.ChildRel.Trim().Replace('/', Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var batchLog = new StringBuilder();
        var summary = DependentUponCsproj.ApplyBatch(csprojPath, deduped, dryRun, batchLog);
        log.Append(batchLog);
        log.AppendLineInvariant($"  {summary}");
    }
}
