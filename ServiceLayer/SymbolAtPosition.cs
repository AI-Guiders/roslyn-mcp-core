using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Символ в позиции (строка, столбец). Без solution — по синтаксису; с solution_or_project_path — семантика и квалифицированное имя. 1-based.</summary>
public static class SymbolAtPosition
{
    /// <summary>Семантический вариант: загружает solution, возвращает kind, name, location и Qualified (ToDisplayString).</summary>
    public static async Task<string> GetSymbolAtPositionAsync(
        string filePath,
        int line,
        int column,
        string? solutionOrProjectPath,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(solutionOrProjectPath) && File.Exists(solutionOrProjectPath))
        {
            var result = await GetSymbolWithSemanticAsync(filePath, line, column, solutionOrProjectPath!.Trim(), cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
        }
        return GetSymbolAtPosition(filePath, line, column, cancellationToken);
    }

    private static async Task<string?> GetSymbolWithSemanticAsync(string filePath, int line, int column, string solutionOrProjectPath, CancellationToken ct)
    {
        var targetPath = NormalizePath(filePath);
        Solution? solution = null;
        try
        {
            var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, ct).ConfigureAwait(false);
            if (solution is null) return null;

            var document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(NormalizePath(d.FilePath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase));
            if (document is null) return null;

            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (root is null || semanticModel is null) return null;

            var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
            var lines = sourceText.Lines;
            if (line < 1 || line > lines.Count) return null;
            var lineInfo = lines[line - 1];
            var columnIndex = column - 1;
            if (columnIndex < 0) return null;
            var lineLen = lineInfo.Span.Length;
            var position = lineLen == 0 ? lineInfo.Start : lineInfo.Start + Math.Min(columnIndex, lineLen);

            var node = root.FindToken(position, findInsideTrivia: true).Parent;
            ISymbol? symbol = null;
            while (node != null)
            {
                ct.ThrowIfCancellationRequested();
                symbol = semanticModel.GetDeclaredSymbol(node, ct) ?? semanticModel.GetSymbolInfo(node, ct).Symbol;
                if (symbol != null) break;
                node = node.Parent;
            }
            if (symbol is null) return null;

            var kind = symbol.Kind.ToString().ToLowerInvariant();
            var name = symbol.Name;
            var format = new SymbolDisplayFormat(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
            var qualified = symbol.ToDisplayString(format);
            var docPath = document.FilePath ?? filePath;
            return $"# Document: {docPath} (total_lines={lines.Count}, line_{line}_length={lineLen})\n{kind}\t{name}\t{filePath}:{line}:{column}\tQualified: {qualified}";
        }
        catch (InvalidOperationException) { return null; }
        finally { solution?.Workspace.Dispose(); }
    }

    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar)) p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    public static string GetSymbolAtPosition(string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";

        var sourceText = SourceText.From(File.ReadAllText(filePath));
        var tree = CSharpSyntaxTree.ParseText(sourceText, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var lines = sourceText.Lines;

        if (line < 1 || line > lines.Count)
            return $"Error: line {line} out of range (1..{lines.Count}).";
        var lineInfo = lines[line - 1];
        var columnIndex = column - 1;
        if (columnIndex < 0)
            return $"Error: column {column} must be >= 1.";
        var lineLen = lineInfo.Span.Length;
        var position = lineLen == 0 ? lineInfo.Start : lineInfo.Start + Math.Min(columnIndex, lineLen);

        var token = root.FindToken(position, findInsideTrivia: true);
        var node = token.Parent;
        while (node != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (kind, name) = GetKindAndName(node);
            if (kind != null && name != null)
                return $"{kind}\t{name}\t{filePath}:{line}:{column}";
            node = node.Parent;
        }

        return $"No symbol at {filePath}:{line}:{column} (token: {token.Kind()}).";
    }

    private static (string? kind, string? name) GetKindAndName(SyntaxNode node) => node switch
    {
        NamespaceDeclarationSyntax n => ("namespace", n.Name.ToString()),
        FileScopedNamespaceDeclarationSyntax n => ("namespace", n.Name.ToString()),
        ClassDeclarationSyntax n => ("class", n.Identifier.Text),
        StructDeclarationSyntax n => ("struct", n.Identifier.Text),
        InterfaceDeclarationSyntax n => ("interface", n.Identifier.Text),
        RecordDeclarationSyntax n => ("record", n.Identifier.Text),
        MethodDeclarationSyntax n => ("method", n.Identifier.Text),
        ConstructorDeclarationSyntax n => ("constructor", n.Identifier.Text),
        PropertyDeclarationSyntax n => ("property", n.Identifier.Text),
        FieldDeclarationSyntax n => ("field", n.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? ""),
        EventDeclarationSyntax n => ("event", n.Identifier.Text),
        EnumDeclarationSyntax n => ("enum", n.Identifier.Text),
        DelegateDeclarationSyntax n => ("delegate", n.Identifier.Text),
        VariableDeclaratorSyntax n => ("local", n.Identifier.Text),
        ParameterSyntax n => ("parameter", n.Identifier.Text),
        TypeParameterSyntax n => ("type_parameter", n.Identifier.Text),
        _ => (null, null)
    };
}
