using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Solution-wide symbol rename. preview (<c>apply=false</c>) — list of changes only; <c>apply=true</c> — write files.
/// Returns <see cref="ToolStepJson"/> (<c>kind=roslyn.rename</c>).
/// </summary>
public static partial class RenameSymbol
{
    public const string Kind = "roslyn.rename";

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
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");
        if (!File.Exists(filePath))
            return ToolStepJson.Fail(Kind, $"file not found: {filePath}");
        if (string.IsNullOrWhiteSpace(newName))
            return ToolStepJson.Fail(Kind, "new_name is required.");

        var targetPath = NormalizePath(filePath);
        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);

            if (solution is null)
                return ToolStepJson.Fail(Kind, "failed to open solution.");

            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));
            if (document is null)
                return ToolStepJson.Fail(Kind, $"file not found in solution: {filePath}");

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return ToolStepJson.Fail(Kind, "could not get syntax/semantic model.");

            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var lines = sourceText.Lines;
            if (line < 1 || line > lines.Count)
                return ToolStepJson.Fail(Kind, $"line {line} out of range (1..{lines.Count}).");
            var lineInfo = lines[line - 1];
            var columnIndex = column - 1;
            if (columnIndex < 0)
                return ToolStepJson.Fail(Kind, $"column {column} must be >= 1.");
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
                return ToolStepJson.Fail(Kind, $"No symbol at {filePath}:{line}:{column}.");

            var oldTypeName = symbol.Name;
            var options = new SymbolRenameOptions(renameOverloads, renameInStrings, renameInComments, renameFile);
            var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken).ConfigureAwait(false);

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
            string? partialNote = null;
            if (renamePartialTypeFiles)
            {
                if (!IsTopLevelNamedTypeForPartialFileRename(symbol))
                {
                    partialNote = "rename_partial_type_files skipped (not a top-level class/struct/interface).";
                }
                else
                {
                    var proj = newSolution.GetProject(document.Project.Id);
                    if (proj is null)
                    {
                        partialNote = "rename_partial_type_files skipped (project not found in new solution).";
                    }
                    else
                    {
                        partialRenames = [];
                        foreach (var doc in proj.Documents)
                        {
                            var p = doc.FilePath;
                            if (p is null || !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (TryComputeRenamedTypeFilePath(p, oldTypeName, newName, out var newP))
                                partialRenames.Add((p, newP));
                        }

                        if (partialRenames.Count == 0)
                            partialNote = "rename_partial_type_files: no matching TypeName.cs / TypeName.*.cs files in project.";
                    }
                }
            }

            object? PartialRenameData() =>
                partialRenames is { Count: > 0 }
                    ? partialRenames.Select(x => new { from = x.OldPath, to = x.NewPath }).ToList()
                    : null;

            object OkData(string summary, IReadOnlyList<string> files) => new
            {
                apply,
                old_name = oldTypeName,
                new_name = newName,
                symbol_kind = symbol.Kind.ToString(),
                files,
                partial_renames = PartialRenameData(),
                summary
            };

            if (changed.Count == 0)
            {
                var summary = string.IsNullOrEmpty(partialNote)
                    ? "No text changes."
                    : $"No text changes. {partialNote}";
                return ToolStepJson.Ok(Kind, summary, OkData(summary, []));
            }

            var files = new List<string>(changed.Count);
            foreach (var doc in changed)
            {
                if (apply)
                {
                    var newText = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(doc.FilePath!, newText.ToString(), cancellationToken).ConfigureAwait(false);
                }

                files.Add(doc.FilePath!);
            }

            if (apply)
            {
                var notes = new List<string>();
                if (!string.IsNullOrEmpty(partialNote))
                    notes.Add(partialNote);

                if (renamePartialTypeFiles && partialRenames is { Count: > 0 })
                {
                    foreach (var (oldPath, newPath) in partialRenames.OrderByDescending(x => x.OldPath.Length))
                    {
                        try
                        {
                            if (!File.Exists(oldPath) && File.Exists(newPath))
                            {
                                notes.Add($"(already at target, skip) {newPath}");
                                continue;
                            }

                            if (!File.Exists(oldPath))
                            {
                                notes.Add($"source missing, skip: {oldPath}");
                                continue;
                            }

                            if (File.Exists(newPath))
                            {
                                notes.Add($"target exists: {newPath}");
                                continue;
                            }

                            File.Move(oldPath, newPath);
                            notes.Add($"{oldPath} → {newPath}");
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"{oldPath}: {ex.Message}");
                        }
                    }
                }

                var summary = notes.Count == 0
                    ? $"Renamed {oldTypeName} → {newName}. Applied text to {files.Count} file(s)."
                    : $"Renamed {oldTypeName} → {newName}. Applied text to {files.Count} file(s). {string.Join(" ", notes)}";
                return ToolStepJson.Ok(Kind, summary, OkData(summary, files));
            }

            {
                var summary = string.IsNullOrEmpty(partialNote)
                    ? $"Would rename {oldTypeName} → {newName} in {files.Count} file(s). Call with apply: true to write."
                    : $"Would rename {oldTypeName} → {newName} in {files.Count} file(s). Call with apply: true to write. {partialNote}";
                return ToolStepJson.Ok(Kind, summary, OkData(summary, files));
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return ToolStepJson.Fail(Kind, ".slnx format is not supported. Use .sln or open by .csproj.");
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }
}
