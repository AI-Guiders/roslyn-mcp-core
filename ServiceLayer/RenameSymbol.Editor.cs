using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

public static partial class RenameSymbol
{
    public static async Task<RoslynEditorApplyResult> RenameForEditorAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string newName,
        string? liveDocumentText = null,
        bool renameInComments = false,
        bool renameInStrings = false,
        bool renameOverloads = false,
        bool renameFile = false,
        bool renamePartialTypeFiles = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return new RoslynEditorApplyResult { Error = $"solution/project not found: {solutionOrProjectPath}" };
        if (!File.Exists(filePath))
            return new RoslynEditorApplyResult { Error = $"file not found: {filePath}" };
        if (string.IsNullOrWhiteSpace(newName))
            return new RoslynEditorApplyResult { Error = "new_name is required." };

        var targetPath = NormalizePath(filePath);
        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken)
                .ConfigureAwait(false);
            if (solution is null)
                return new RoslynEditorApplyResult { Error = "failed to open solution." };

            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));
            if (document is null)
                return new RoslynEditorApplyResult { Error = $"file not found in solution: {filePath}" };

            if (!string.IsNullOrEmpty(liveDocumentText))
            {
                document = document.WithText(SourceText.From(liveDocumentText));
                solution = document.Project.Solution;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return new RoslynEditorApplyResult { Error = "could not get syntax/semantic model." };

            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var lines = sourceText.Lines;
            if (line < 1 || line > lines.Count)
                return new RoslynEditorApplyResult { Error = $"line {line} out of range (1..{lines.Count})." };
            var lineInfo = lines[line - 1];
            var columnIndex = column - 1;
            if (columnIndex < 0)
                return new RoslynEditorApplyResult { Error = $"column {column} must be >= 1." };
            var lineLen = lineInfo.Span.Length;
            var position = lineLen == 0 ? lineInfo.Start : lineInfo.Start + Math.Min(columnIndex, lineLen);

            var node = root.FindToken(position, findInsideTrivia: true).Parent;
            ISymbol? symbol = null;
            while (node is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken)
                    ?? semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol is not null)
                    break;
                node = node.Parent;
            }

            if (symbol is null)
                return new RoslynEditorApplyResult { Error = $"No symbol at {filePath}:{line}:{column}." };

            var oldTypeName = symbol.Name;
            var options = new SymbolRenameOptions(renameOverloads, renameInStrings, renameInComments, renameFile);
            var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken)
                .ConfigureAwait(false);

            var changes = new List<RoslynEditorDocumentChange>();
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
                    {
                        changes.Add(new RoslynEditorDocumentChange(
                            NormalizePath(doc.FilePath),
                            newText.ToString(),
                            IsNewFile: false));
                    }
                }
            }

            if (renamePartialTypeFiles && IsTopLevelNamedTypeForPartialFileRename(symbol))
            {
                var proj = newSolution.GetProject(document.Project.Id);
                if (proj is not null)
                {
                    foreach (var doc in proj.Documents)
                    {
                        var p = doc.FilePath;
                        if (p is null || !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TryComputeRenamedTypeFilePath(p, oldTypeName, newName, out var newP))
                            continue;
                        var normalizedOld = NormalizePath(p);
                        var normalizedNew = NormalizePath(newP);
                        var textChange = changes.FirstOrDefault(c =>
                            string.Equals(c.FilePath, normalizedOld, StringComparison.OrdinalIgnoreCase));
                        if (textChange is not null)
                        {
                            changes.Remove(textChange);
                            changes.Add(textChange with { FilePath = normalizedNew, PreviousFilePath = normalizedOld });
                        }
                    }
                }
            }

            return new RoslynEditorApplyResult { Changes = changes };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx", StringComparison.OrdinalIgnoreCase))
        {
            return new RoslynEditorApplyResult { Error = ".slnx format is not supported. Use .sln or .csproj." };
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }
}
