using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// In-proc document format via <see cref="Formatter"/> (not <c>dotnet format</c> CLI).
/// Respects workspace / .editorconfig options when present.
/// Returns <see cref="ToolStepJson"/> (<c>kind=roslyn.format</c>).
/// </summary>
public static class FormatDocument
{
    public const string Kind = "roslyn.format";

    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <param name="apply">true — write file; false — report whether text would change.</param>
    /// <param name="aggressive">
    /// true — <c>SyntaxNode.NormalizeWhitespace</c> (rewrites indent aggressively; good after Extract Method).
    /// false — <see cref="Formatter.FormatAsync"/> only (closer to IDE Format Document).
    /// </param>
    public static async Task<string> FormatAsync(
        string solutionOrProjectPath,
        string filePath,
        bool apply = true,
        bool aggressive = false,
        int? line = null,
        int? column = null,
        int? endLine = null,
        int? endColumn = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");
        if (!File.Exists(filePath))
            return ToolStepJson.Fail(Kind, $"file not found: {filePath}");

        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken)
                .ConfigureAwait(false);
            if (solution is null)
                return ToolStepJson.Fail(Kind, "failed to open solution");

            var targetPath = NormalizePath(filePath);
            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));

            if (document is null)
                return ToolStepJson.Fail(Kind, $"document not in workspace: {filePath}");

            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            Document formattedDoc;
            string scopeLabel;

            if (aggressive)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                    return ToolStepJson.Fail(Kind, "no syntax root");
                var style = EditorConfigStyle.GetOptionsForDirectory(Path.GetDirectoryName(filePath) ?? ".");
                var normalized = root.NormalizeWhitespace(indentation: style.IndentString, eol: style.NewLine);
                formattedDoc = document.WithSyntaxRoot(normalized);
                formattedDoc = await Formatter.FormatAsync(formattedDoc, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                scopeLabel = "document+normalize";
            }
            else if (line is >= 1 && column is >= 1 && endLine is >= 1 && endColumn is >= 1)
            {
                var start = sourceText.Lines[Math.Clamp(line.Value - 1, 0, sourceText.Lines.Count - 1)]
                    .Start + Math.Max(0, column.Value - 1);
                var endLineObj = sourceText.Lines[Math.Clamp(endLine.Value - 1, 0, sourceText.Lines.Count - 1)];
                var end = endLineObj.Start + Math.Min(Math.Max(0, endColumn.Value - 1), endLineObj.Span.Length);
                if (end < start)
                    (start, end) = (end, start);
                var span = TextSpan.FromBounds(start, end);
                formattedDoc = await Formatter.FormatAsync(document, span, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                scopeLabel = $"span L{line}:{column}-L{endLine}:{endColumn}";
            }
            else
            {
                formattedDoc = await Formatter.FormatAsync(document, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                scopeLabel = "document";
            }

            var newText = await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changed = !sourceText.ContentEquals(newText);

            var data = new
            {
                document = document.FilePath,
                scope = scopeLabel,
                apply,
                aggressive,
                changed,
                would_change = !apply && changed ? true : (bool?)null
            };

            if (!changed)
                return ToolStepJson.Ok(Kind, "No formatting changes.", data);

            if (apply)
            {
                if (document.FilePath is null)
                    return ToolStepJson.Fail(Kind, "document has no FilePath", data);
                await File.WriteAllTextAsync(document.FilePath, newText.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                return ToolStepJson.Ok(Kind, "Formatted. File updated.", data);
            }

            return ToolStepJson.Ok(Kind, "Would change (dry run).", data);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return ToolStepJson.Fail(Kind, ".slnx format is not supported. Use .sln or .csproj.");
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }
}
