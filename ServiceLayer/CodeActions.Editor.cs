using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

public sealed record RoslynEditorCodeAction(int Index, string Title, string Kind);

public sealed record RoslynEditorDocumentChange(
    string FilePath,
    string Text,
    bool IsNewFile,
    string? PreviousFilePath = null);

public sealed class RoslynEditorCodeActionsResult
{
    public IReadOnlyList<RoslynEditorCodeAction> Actions { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class RoslynEditorApplyResult
{
    public IReadOnlyList<RoslynEditorDocumentChange> Changes { get; init; } = [];
    public string? Error { get; init; }
}

public static partial class CodeActions
{
    private sealed record PreparedEditorSession(Solution Solution, Document Document, TextSpan Span);

    private static async Task<(PreparedEditorSession? Session, string? Error)> PrepareEditorSessionAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string? liveDocumentText,
        int? endLine,
        int? endColumn,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(solutionOrProjectPath))
            return (null, $"solution/project not found: {solutionOrProjectPath}");
        if (!File.Exists(filePath))
            return (null, $"file not found: {filePath}");

        var targetPath = NormalizePath(filePath);
        var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
        var solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken)
            .ConfigureAwait(false);
        if (solution is null)
            return (null, "failed to open solution.");

        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return (null, $"file not found in solution: {filePath}");

        if (!string.IsNullOrEmpty(liveDocumentText))
        {
            document = document.WithText(SourceText.From(liveDocumentText));
            solution = document.Project.Solution;
        }

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var lines = sourceText.Lines;
        if (line < 1 || line > lines.Count)
            return (null, $"line {line} out of range (1..{lines.Count}).");
        var lineInfo = lines[line - 1];
        var columnIndex = column - 1;
        var lineLen = lineInfo.Span.Length;
        if (columnIndex < 0)
            return (null, $"column {column} must be >= 1.");
        var position = lineLen == 0
            ? lineInfo.Start
            : lineInfo.Start + Math.Min(columnIndex, lineLen);
        TextSpan span;
        if (endLine.HasValue && endColumn.HasValue
            && TryGetSpanFromRange(lines, line, column, endLine.Value, endColumn.Value) is { } rangeSpan)
        {
            span = rangeSpan;
        }
        else
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            span = root is null
                ? new TextSpan(position, 0)
                : root.FindToken(position, findInsideTrivia: true).Span;
        }

        return (new PreparedEditorSession(solution, document, span), null);
    }

    private static async Task<List<(int index, string title, CodeAction action, CodeFixProvider? provider, Diagnostic? diagnostic)>> CollectActionsAsync(
        PreparedEditorSession session,
        CancellationToken cancellationToken)
    {
        var document = session.Document;
        var span = session.Span;
        var targetPath = NormalizePath(document.FilePath ?? "");
        var actions = new List<(int index, string title, CodeAction action, CodeFixProvider? provider, Diagnostic? diagnostic)>();
        var index = 0;

        foreach (var provider in GetRefactoringProviders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collected = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, span, a => collected.Add(a), cancellationToken);
            try
            {
                await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
            }
            catch { /* skip */ }

            foreach (var a in collected)
            foreach (var (title, action) in FlattenAction(a))
                actions.Add((index++, title, action, null, null));
        }

        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var diagnosticsForFile = await GetDiagnosticsInSpanAsync(document.Project, compilation, targetPath, span, cancellationToken)
            .ConfigureAwait(false);
        foreach (var provider in GetCodeFixProviders(document.Project))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixableIds = provider.FixableDiagnosticIds;
            foreach (var diagnostic in diagnosticsForFile)
            {
                if (!fixableIds.Contains(diagnostic.Id))
                    continue;
                var collected = new List<CodeAction>();
                var context = new CodeFixContext(document, diagnostic, (a, _) => collected.Add(a), cancellationToken);
                try
                {
                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch { /* skip */ }

                foreach (var a in collected)
                foreach (var (title, action) in FlattenAction(a))
                    actions.Add((index++, title, action, provider, diagnostic));
            }
        }

        return actions;
    }

    private static string InferActionKind(string title)
    {
        if (title.Contains("Extract interface", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Extract Interface", StringComparison.Ordinal))
            return "refactor.extract.interface";
        if (title.Contains("Move", StringComparison.OrdinalIgnoreCase))
            return "refactor.move";
        if (title.Contains("Rename", StringComparison.OrdinalIgnoreCase))
            return "refactor.rename";
        return "refactor.rewrite";
    }

    private static async Task<IReadOnlyList<RoslynEditorDocumentChange>> CollectDocumentChangesAsync(
        Solution oldSolution,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        var changes = new List<RoslynEditorDocumentChange>();
        var oldPaths = oldSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .ToDictionary(d => NormalizePath(d.FilePath!), d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var project in newSolution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null)
                    continue;
                var path = NormalizePath(doc.FilePath);
                var newText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                oldPaths.TryGetValue(path, out var oldDoc);
                if (oldDoc is null)
                {
                    changes.Add(new RoslynEditorDocumentChange(path, newText, IsNewFile: true));
                    continue;
                }

                var oldText = await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!oldText.ContentEquals(await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)))
                    changes.Add(new RoslynEditorDocumentChange(path, newText, IsNewFile: false));
            }
        }

        return changes;
    }

    public static async Task<RoslynEditorCodeActionsResult> ListForEditorAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string? liveDocumentText = null,
        int? endLine = null,
        int? endColumn = null,
        CancellationToken cancellationToken = default)
    {
        Solution? solution = null;
        try
        {
            var (session, error) = await PrepareEditorSessionAsync(
                solutionOrProjectPath, filePath, line, column, liveDocumentText, endLine, endColumn, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
                return new RoslynEditorCodeActionsResult { Error = error };

            solution = session.Solution;
            var collected = await CollectActionsAsync(session, cancellationToken).ConfigureAwait(false);
            var items = collected
                .Select(a => new RoslynEditorCodeAction(a.index, a.title, InferActionKind(a.title)))
                .ToList();
            return new RoslynEditorCodeActionsResult { Actions = items };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx", StringComparison.OrdinalIgnoreCase))
        {
            return new RoslynEditorCodeActionsResult { Error = ".slnx format is not supported. Use .sln or .csproj." };
        }
        finally
        {
            solution?.Workspace.Dispose();
        }
    }

    public static async Task<RoslynEditorApplyResult> ApplyForEditorAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        int actionIndex,
        string? liveDocumentText = null,
        int? endLine = null,
        int? endColumn = null,
        CancellationToken cancellationToken = default)
    {
        Solution? solution = null;
        try
        {
            var (session, error) = await PrepareEditorSessionAsync(
                solutionOrProjectPath, filePath, line, column, liveDocumentText, endLine, endColumn, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
                return new RoslynEditorApplyResult { Error = error };

            solution = session.Solution;
            var oldSolution = solution;
            var collected = await CollectActionsAsync(session, cancellationToken).ConfigureAwait(false);
            if (actionIndex < 0 || actionIndex >= collected.Count)
                return new RoslynEditorApplyResult { Error = $"action_index {actionIndex} out of range (0..{collected.Count - 1})." };

            var chosen = collected[actionIndex].action;
            var operations = await chosen.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var op in operations)
            {
                if (op is not ApplyChangesOperation applyOp)
                    continue;
                var newSolution = applyOp.ChangedSolution;
                var changes = await CollectDocumentChangesAsync(oldSolution, newSolution, cancellationToken).ConfigureAwait(false);
                return new RoslynEditorApplyResult { Changes = changes };
            }

            return new RoslynEditorApplyResult { Error = "code action produced no document changes." };
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
