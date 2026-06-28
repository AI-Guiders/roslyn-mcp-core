using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Разрешение места для брейкпоинта: по имени символа (метод, свойство и т.п.) в файле возвращает file:line первой исполняемой строки.</summary>
public static class ResolveBreakpoint
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <summary>Возвращает номер строки (1-based) для узла по дереву.</summary>
    private static int GetLine(SyntaxNode node, SyntaxTree tree)
    {
        var lineSpan = tree.GetLineSpan(node.Span);
        return lineSpan.StartLinePosition.Line + 1;
    }

    /// <summary>Первая исполняемая строка для метода: первый оператор в теле или строка expression body.</summary>
    private static SyntaxNode? GetFirstExecutableNode(BaseMethodDeclarationSyntax method)
    {
        if (method.Body != null)
        {
            var first = method.Body.Statements.FirstOrDefault();
            if (first != null) return first;
            return method.Body.OpenBraceToken.Parent;
        }
        if (method.ExpressionBody?.Expression != null)
            return method.ExpressionBody.Expression;
        return null;
    }

    private static SyntaxNode? GetFirstExecutableNode(PropertyDeclarationSyntax prop)
    {
        if (prop.ExpressionBody != null)
            return prop.ExpressionBody.Expression;
        var accessor = prop.AccessorList?.Accessors.FirstOrDefault(a => a.Body != null);
        if (accessor?.Body?.Statements.FirstOrDefault() is { } st)
            return st;
        if (accessor?.Body != null)
            return accessor.Body.OpenBraceToken.Parent;
        return null;
    }

    private static SyntaxNode? GetFirstExecutableNode(IndexerDeclarationSyntax indexer)
    {
        if (indexer.ExpressionBody != null)
            return indexer.ExpressionBody.Expression;
        var accessor = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.Body != null || a.ExpressionBody != null);
        if (accessor?.ExpressionBody?.Expression != null)
            return accessor.ExpressionBody.Expression;
        if (accessor?.Body?.Statements.FirstOrDefault() is { } st)
            return st;
        if (accessor?.Body != null)
            return accessor.Body.OpenBraceToken.Parent;
        return null;
    }

    public static async Task<string> ResolveAsync(
        string solutionOrProjectPath,
        string filePath,
        string symbolName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";
        if (string.IsNullOrWhiteSpace(symbolName))
            return "Error: symbol_name is required.";

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
            if (root is null)
                return "Error: could not get syntax root.";

            var tree = root.SyntaxTree;
            var name = symbolName.Trim();
            var results = new List<(string kind, int line)>();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(method.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = GetFirstExecutableNode(method);
                if (node != null)
                    results.Add(("method", GetLine(node, tree)));
            }

            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ctorName = ctor.Identifier.ValueText;
                if (!string.Equals(ctorName, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = GetFirstExecutableNode(ctor);
                if (node != null)
                    results.Add(("constructor", GetLine(node, tree)));
            }

            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(prop.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = GetFirstExecutableNode(prop);
                if (node != null)
                    results.Add(("property", GetLine(node, tree)));
            }

            foreach (var indexer in root.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals("this", name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = GetFirstExecutableNode(indexer);
                if (node != null)
                    results.Add(("indexer", GetLine(node, tree)));
            }

            var docPath = document.FilePath ?? filePath;
            if (results.Count == 0)
                return $"# Resolve breakpoint: no executable location found for symbol \"{name}\" in {docPath}.\n# Searched: methods, constructors, properties, indexers (use \"this\" for indexer).";

            var sb = new StringBuilder();
            sb.AppendLine("# Breakpoint location(s) — first executable line of symbol");
            sb.AppendLineInvariant($"# Symbol: \"{name}\" in {docPath}");
            sb.AppendLine();
            foreach (var (kind, line) in results.Distinct().OrderBy(t => t.line))
                sb.AppendLineInvariant($"{docPath}:{line}\t({kind})");
            sb.AppendLine().AppendLineInvariant($"Total: {results.Count} location(s). Use file:line to set breakpoint.");
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
