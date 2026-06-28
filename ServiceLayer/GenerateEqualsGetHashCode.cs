using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Генерация override Equals и GetHashCode по выбранным полям/свойствам без диалога Roslyn.</summary>
public static class GenerateEqualsGetHashCode
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    private static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.None,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName);

    /// <summary>По позиции на класс генерирует override Equals(object?) и GetHashCode() по выбранным полям/свойствам. Опционально: member_names, insert_into_file.</summary>
    public static async Task<string> GenerateEqualsGetHashCodeAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        IReadOnlyList<string>? memberNames = null,
        bool insertIntoFile = false,
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
                return "Error: column must be >= 1.";
            var position = lineInfo.Span.Length == 0
                ? lineInfo.Start
                : lineInfo.Start + Math.Min(columnIndex, lineInfo.Span.Length);

            var node = root.FindToken(position, findInsideTrivia: true).Parent;
            INamedTypeSymbol? typeSymbol = null;
            SyntaxNode? classDeclaration = null;
            while (node != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sym = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (sym is INamedTypeSymbol named && named.TypeKind == TypeKind.Class)
                {
                    typeSymbol = named;
                    classDeclaration = node;
                    break;
                }
                node = node.Parent;
            }

            if (typeSymbol is null)
                return $"Error: no class at {filePath}:{line}:{column}. Position the cursor on the class name or inside the class body.";

            var style = EditorConfigStyle.GetOptionsForDirectory(Path.GetDirectoryName(document.FilePath) ?? "");

            var memberSet = memberNames != null && memberNames.Count > 0
                ? new HashSet<string>(memberNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase)
                : null;

            var members = new List<string>();
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;
                if (memberSet != null && !memberSet.Contains(member.Name))
                    continue;

                switch (member)
                {
                    case IFieldSymbol field when field.IsConst == false && field.IsStatic == false:
                        members.Add(field.Name);
                        break;
                    case IPropertySymbol prop when prop.IsIndexer == false:
                        members.Add(prop.Name);
                        break;
                }
            }

            if (members.Count == 0)
                return "Error: no instance fields or properties to include. Add member_names or ensure the class has instance fields/properties.";

            var indent = "\t\t";
            var className = typeSymbol.Name;
            var equalsBody = BuildEqualsBody(className, members);
            var block = $"public override bool Equals(object? obj) => obj is {className} other && {equalsBody};" + Environment.NewLine + indent +
                $"public override int GetHashCode() => HashCode.Combine({string.Join(", ", members)});";

            if (insertIntoFile && classDeclaration != null)
            {
                var (closeBrace, indentBeforeBrace) = GetClassCloseBraceAndIndent(classDeclaration, root);
                if (closeBrace != null)
                {
                    var insertIndent = indentBeforeBrace ?? style.IndentString;
                    var toInsert = Environment.NewLine + insertIndent +
                        $"public override bool Equals(object? obj) => obj is {className} other && {equalsBody};" +
                        Environment.NewLine + insertIndent +
                        $"public override int GetHashCode() => HashCode.Combine({string.Join(", ", members)});" +
                        Environment.NewLine + insertIndent;
                    var text = root.GetText();
                    var change = new TextChange(new TextSpan(closeBrace.Value.Span.Start, 0), toInsert);
                    var newText = text.WithChanges(change);
                    var newSolution = solution.WithDocumentText(document.Id, newText);
                    var applied = newSolution.Workspace.TryApplyChanges(newSolution);
                    if (applied)
                    {
                        newSolution.Workspace.Dispose();
                        return $"# Equals/GetHashCode inserted into {filePath}\n\n{block}";
                    }
                }
            }

            solution?.Workspace.Dispose();
            return $"# Generated Equals and GetHashCode for {className}\n# Paste into the class body. (Use insert_into_file: true to insert automatically.)\n\n{block}";
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

    private static string BuildEqualsBody(string className, List<string> members)
    {
        return string.Join(" && ", members.Select(m => $"{m} == other.{m}"));
    }

    private static (SyntaxToken? closeBrace, string? indent) GetClassCloseBraceAndIndent(SyntaxNode classDeclaration, SyntaxNode root)
    {
        if (classDeclaration is not ClassDeclarationSyntax classSyn)
            return (null, null);
        var closeBrace = classSyn.CloseBraceToken;
        if (closeBrace.IsMissing)
            return (null, null);
        var text = root.SyntaxTree?.GetText();
        if (text is null)
            return (null, null);
        var line = text.Lines.GetLineFromPosition(closeBrace.Span.Start);
        var lineText = line.ToString();
        var posInLine = closeBrace.Span.Start - line.Start;
        var indent = posInLine > 0 && posInLine <= lineText.Length ? lineText[..posInLine] : "";
        if (string.IsNullOrEmpty(indent))
            indent = "\t";
        return (closeBrace, indent);
    }
}
