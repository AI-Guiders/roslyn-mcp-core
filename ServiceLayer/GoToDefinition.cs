using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Переход к определению символа: по позиции в файле возвращает file:line:column объявления (объявлений для partial и т.п.).</summary>
public static class GoToDefinition
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    public static async Task<string> GoToDefinitionAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";

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
            SyntaxNode? symbolNode = null;
            while (node != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken) ?? semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol != null)
                {
                    symbolNode = node;
                    break;
                }
                node = node.Parent;
            }
            if (symbol is null)
                return $"No symbol at {filePath}:{line}:{column}.";

            // Если нашли метод и позиция в левой части X.Y (до точки), переходим к определению типа X
            if (symbol is IMethodSymbol && symbolNode != null)
            {
                var memberAccess = symbolNode.AncestorsAndSelf()
                    .OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault(ma => position < ma.OperatorToken.Span.Start);
                if (memberAccess != null)
                {
                    var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken);
                    if (typeInfo.Type is INamedTypeSymbol typeSymbol && typeSymbol.Locations.Any(l => l.IsInSource))
                        symbol = typeSymbol;
                }
            }

            // Для алиаса (using X = Y) берём целевой символ
            if (symbol is IAliasSymbol alias)
                symbol = alias.Target;

            var defLocations = symbol.Locations.Where(loc => loc.IsInSource && loc.SourceTree?.FilePath != null).ToList();
            if (defLocations.Count == 0)
                return $"Definition not in source (e.g. metadata): {symbol.Kind} {symbol.Name}.";

            var sb = new StringBuilder();
            var docPath = document.FilePath ?? filePath;
            sb.AppendLineInvariant($"# Document: {docPath} (total_lines={lines.Count}, line_{line}_length={lineLen})");
            sb.AppendLineInvariant($"# Definition(s) of {symbol.Kind} {symbol.Name}");
            sb.AppendLine();
            foreach (var loc in defLocations)
            {
                if (loc.SourceTree?.FilePath is null) continue;
                var span = loc.GetLineSpan();
                sb.AppendLineInvariant($"{loc.SourceTree.FilePath}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}");
            }
            sb.AppendLine().AppendLineInvariant($"Total: {defLocations.Count} definition(s)");
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
