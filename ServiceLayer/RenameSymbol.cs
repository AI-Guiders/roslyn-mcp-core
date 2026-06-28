using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Переименование символа по solution. preview (apply=false) — только список изменений; apply=true — запись в файлы.</summary>
public static partial class RenameSymbol
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <summary>Топ-уровневый class/struct/interface: можно согласовать имена partial-файлов TypeName.cs и TypeName.*.cs.</summary>
    private static bool IsTopLevelNamedTypeForPartialFileRename(ISymbol symbol) =>
        symbol is INamedTypeSymbol { ContainingType: null } nt
        && nt.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface;

    /// <summary>
    /// Переименовать путь к файлу с префиксом имени типа: <c>TypeName.cs</c>, <c>TypeName.Part.cs</c> → <c>NewName...</c>.
    /// </summary>
    private static bool TryComputeRenamedTypeFilePath(string fullPath, string oldTypeName, string newTypeName, out string newFullPath)
    {
        newFullPath = "";
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return false;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var dir = Path.GetDirectoryName(fullPath) ?? "";

        if (string.Equals(baseName, oldTypeName, StringComparison.OrdinalIgnoreCase))
        {
            newFullPath = Path.Combine(dir, newTypeName + ".cs");
            return !PathsEqualNormalized(fullPath, newFullPath);
        }

        var prefix = oldTypeName + ".";
        if (baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = baseName.Substring(prefix.Length);
            newFullPath = Path.Combine(dir, newTypeName + "." + rest + ".cs");
            return !PathsEqualNormalized(fullPath, newFullPath);
        }

        return false;
    }

    private static bool PathsEqualNormalized(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static async Task<string> RenameAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string newName,
        bool apply,
        bool renameInComments = false,
        bool renameInStrings = false,
        bool renameOverloads = false,
        bool renameFile = false,
        bool renamePartialTypeFiles = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";
        if (string.IsNullOrWhiteSpace(newName))
            return "Error: new_name is required.";

        var targetPath = NormalizePath(filePath);
        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);

            if (solution is null)
                return "Error: failed to open solution.";

            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));
            if (document is null)
                return $"Error: file not found in solution: {filePath}";

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return "Error: could not get syntax/semantic model.";

            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var lines = sourceText.Lines;
            if (line < 1 || line > lines.Count)
                return $"Error: line {line} out of range (1..{lines.Count}).";
            var lineInfo = lines[line - 1];
            var columnIndex = column - 1;
            if (columnIndex < 0)
                return $"Error: column {column} must be >= 1.";
            var lineLen = lineInfo.Span.Length;
            var position = lineLen == 0 ? lineInfo.Start : lineInfo.Start + Math.Min(columnIndex, lineLen);

            var node = root.FindToken(position, findInsideTrivia: true).Parent;
            ISymbol? symbol = null;
            while (node is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken) ?? semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol is not null)
                    break;
                node = node.Parent;
            }
            if (symbol is null)
                return $"No symbol at {filePath}:{line}:{column}.";

            var oldTypeName = symbol.Name;
            var options = new SymbolRenameOptions(renameOverloads, renameInStrings, renameInComments, renameFile);
            var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            var docPath = document.FilePath ?? filePath;
            sb.AppendLineInvariant($"# Document: {docPath} (total_lines={lines.Count}, line_{line}_length={lineLen})");
            sb.AppendLineInvariant($"# Rename {symbol.Kind} {oldTypeName} → {newName}");
            sb.AppendLine();

            var changed = new List<Document>();
            foreach (var project in newSolution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath is null)
                        continue;
                    var oldDoc = solution.GetDocument(doc.Id);
                    if (oldDoc is null)
                        continue;
                    var oldText = await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var newText = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    if (!oldText.ContentEquals(newText))
                        changed.Add(doc);
                }
            }

            List<(string OldPath, string NewPath)>? partialRenames = null;
            if (renamePartialTypeFiles)
            {
                if (!IsTopLevelNamedTypeForPartialFileRename(symbol))
                {
                    sb.AppendLine("# rename_partial_type_files: skipped (not a top-level class/struct/interface).");
                    sb.AppendLine();
                }
                else
                {
                    var proj = newSolution.GetProject(document.Project.Id);
                    partialRenames = [];
                    if (proj is null)
                    {
                        sb.AppendLine("# rename_partial_type_files: skipped (project not found in new solution).");
                        sb.AppendLine();
                    }
                    else
                    {
                        foreach (var doc in proj.Documents)
                        {
                            var p = doc.FilePath;
                            if (p is null || !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (TryComputeRenamedTypeFilePath(p, oldTypeName, newName, out var newP))
                                partialRenames.Add((p, newP));
                        }

                        if (partialRenames.Count == 0)
                        {
                            sb.AppendLine("# rename_partial_type_files: no matching TypeName.cs / TypeName.*.cs files in project.");
                            sb.AppendLine();
                        }
                        else
                        {
                            sb.AppendLine("# rename_partial_type_files (disk path renames after symbol rename):");
                            foreach (var (o, n) in partialRenames)
                                sb.AppendLineInvariant($"  {o} → {n}");
                            sb.AppendLine();
                        }
                    }
                }
            }

            if (changed.Count == 0)
            {
                sb.AppendLine("(no text changes)");
                return sb.ToString();
            }

            var applied = new List<string>();
            foreach (var doc in changed)
            {
                if (apply)
                {
                    var newText = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(doc.FilePath!, newText.ToString(), cancellationToken).ConfigureAwait(false);
                    applied.Add(doc.FilePath!);
                }
                else
                {
                    sb.AppendLine(doc.FilePath);
                }
            }

            if (apply)
            {
                sb.AppendLine("Applied text to:");
                foreach (var p in applied)
                    sb.AppendLine("  " + p);

                if (renamePartialTypeFiles && partialRenames is { Count: > 0 })
                {
                    sb.AppendLine("Partial file renames:");
                    foreach (var (oldPath, newPath) in partialRenames.OrderByDescending(x => x.OldPath.Length))
                    {
                        try
                        {
                            if (!File.Exists(oldPath) && File.Exists(newPath))
                            {
                                sb.AppendLineInvariant($"  (already at target, skip) {newPath}");
                                continue;
                            }

                            if (!File.Exists(oldPath))
                            {
                                sb.AppendLineInvariant($"  Warning: source missing, skip: {oldPath}");
                                continue;
                            }

                            if (File.Exists(newPath))
                            {
                                sb.AppendLineInvariant($"  Error: target exists: {newPath}");
                                continue;
                            }

                            File.Move(oldPath, newPath);
                            sb.AppendLineInvariant($"  {oldPath} → {newPath}");
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLineInvariant($"  Error ({oldPath}): {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine().AppendLineInvariant($"Total: {changed.Count} file(s). Call with apply: true to write.");
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return "Error: .slnx format is not supported. Use .sln or open by .csproj.";
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }
}
