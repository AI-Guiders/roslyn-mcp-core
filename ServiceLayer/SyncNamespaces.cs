using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Приведение объявлений namespace к структуре папок (RootNamespace + путь папки). Опционально dry_run — только отчёт.</summary>
public static class SyncNamespaces
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    private static string? GetRootNamespaceFromProject(string projectFilePath)
    {
        if (!File.Exists(projectFilePath)) return null;
        try
        {
            var text = File.ReadAllText(projectFilePath);
            var match = Regex.Match(text, @"<RootNamespace>\s*([^<]+)\s*</RootNamespace>");
            if (match.Success)
                return match.Groups[1].Value.Trim();
        }
        catch { /* ignore */ }
        return Path.GetFileNameWithoutExtension(projectFilePath);
    }

    /// <summary>Вычисляет целевой namespace по пути файла относительно корня проекта: RootNamespace + папки.</summary>
    private static string GetTargetNamespace(string rootNamespace, string projectDir, string filePath)
    {
        var fullPath = NormalizePath(filePath);
        var projectRoot = NormalizePath(projectDir);
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            return rootNamespace;
        var relative = fullPath.Length == projectRoot.Length
            ? ""
            : fullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
        var dir = Path.GetDirectoryName(relative);
        if (string.IsNullOrEmpty(dir))
            return rootNamespace;
        var segments = dir.Replace('\\', '/').Split('/').Where(s => s.Length > 0).ToArray();
        if (segments.Length == 0)
            return rootNamespace;
        return rootNamespace + "." + string.Join(".", segments);
    }

    public static async Task<string> SyncAsync(
        string solutionOrProjectPath,
        bool dryRun = false,
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";

        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);

            if (solution is null)
                return "Error: failed to open solution.";

            var sb = new StringBuilder();
            sb.AppendLine("# Sync namespaces to folder structure");
            sb.AppendLineInvariant($"# Path: {solutionOrProjectPath}");
            sb.AppendLineInvariant($"# Mode: {(dryRun ? "dry run (no writes)" : "apply")}");
            sb.AppendLine();

            var allChanges = new List<(DocumentId docId, string filePath, string oldNs, string newNs)>();

            var projectsToProcess = solution.Projects.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                var normalizedProjectPath = NormalizePath(projectPath);
                projectsToProcess = projectsToProcess.Where(p => p.FilePath is not null && NormalizePath(p.FilePath) == normalizedProjectPath);
            }

            foreach (var project in projectsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projFilePath = project.FilePath;
                if (string.IsNullOrEmpty(projFilePath))
                    continue;
                var projectDir = Path.GetDirectoryName(projFilePath) ?? projFilePath;
                var rootNs = GetRootNamespaceFromProject(projFilePath) ?? project.Name;

                foreach (var doc in project.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var filePath = doc.FilePath;
                    if (string.IsNullOrEmpty(filePath) || !string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var targetNs = GetTargetNamespace(rootNs, projectDir, filePath);
                    var tree = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    if (tree?.GetRoot(cancellationToken) is not CompilationUnitSyntax root)
                        continue;

                    var nsDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                    if (nsDecl is null)
                        continue;

                    var currentNs = nsDecl.Name.ToString();
                    if (string.Equals(currentNs, targetNs, StringComparison.Ordinal))
                        continue;

                    allChanges.Add((doc.Id, filePath, currentNs, targetNs));
                }
            }

            if (allChanges.Count == 0)
            {
                sb.AppendLine("(no namespace changes needed)");
                return sb.ToString();
            }

            foreach (var (_, filePath, oldNs, newNs) in allChanges)
                sb.AppendLineInvariant($"{filePath}: \"{oldNs}\" → \"{newNs}\"");
            sb.AppendLine();
            sb.AppendLineInvariant($"Total: {allChanges.Count} file(s) to update.");

            if (dryRun)
            {
                sb.AppendLine().AppendLine("# Run without dry_run to apply.");
                return sb.ToString();
            }

            // Применяем: 1) замена namespace в каждом файле; 2) замена using oldNs на using newNs во всех документах проектов
            var solutionWithNewNamespaces = solution;
            var oldToNew = allChanges.Select(c => (c.oldNs, c.newNs)).Distinct().ToList();

            foreach (var (docId, filePath, oldNs, newNs) in allChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var doc = solutionWithNewNamespaces.GetDocument(docId);
                if (doc is null) continue;

                var tree = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                if (tree?.GetRoot(cancellationToken) is not CompilationUnitSyntax root)
                    continue;

                var nsDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                if (nsDecl is null) continue;

                var newName = SyntaxFactory.ParseName(newNs);
                var newNsDecl = nsDecl.WithName(newName);
                var newRoot = root.ReplaceNode(nsDecl, newNsDecl);
                solutionWithNewNamespaces = solutionWithNewNamespaces.WithDocumentSyntaxRoot(docId, newRoot);
            }

            // Во всех документах проектов заменяем using oldNs на using newNs
            foreach (var project in solutionWithNewNamespaces.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tree = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    if (tree?.GetRoot(cancellationToken) is not CompilationUnitSyntax root)
                        continue;

                    var usings = root.Usings;
                    var changed = false;
                    var newUsings = new List<UsingDirectiveSyntax>();
                    foreach (var u in usings)
                    {
                        if (u.Name is null) { newUsings.Add(u); continue; }
                        var nameStr = u.Name.ToString();
                        var replacement = oldToNew.FirstOrDefault(t => string.Equals(t.oldNs, nameStr, StringComparison.Ordinal));
                        if (replacement.oldNs is not null)
                        {
                            newUsings.Add(u.WithName(SyntaxFactory.ParseName(replacement.newNs)));
                            changed = true;
                        }
                        else
                            newUsings.Add(u);
                    }

                    if (!changed) continue;

                    var newRoot = root.WithUsings(SyntaxFactory.List(newUsings));
                    solutionWithNewNamespaces = solutionWithNewNamespaces.WithDocumentSyntaxRoot(doc.Id, newRoot);
                }
            }

            // Записываем все изменённые файлы на диск
            var changes = solutionWithNewNamespaces.GetChanges(solution);
            var changedDocIds = new HashSet<DocumentId>();
            foreach (var projectChange in changes.GetProjectChanges())
                foreach (var docId in projectChange.GetChangedDocuments())
                    changedDocIds.Add(docId);

            foreach (var docId in changedDocIds)
            {
                var doc = solutionWithNewNamespaces.GetDocument(docId);
                if (doc?.FilePath is null) continue;
                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(doc.FilePath, text.ToString(), cancellationToken).ConfigureAwait(false);
            }

            sb.AppendLine().AppendLine("Applied. Files written to disk.");
            return sb.ToString();
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
