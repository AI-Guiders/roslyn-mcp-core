using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Генерация конструктора по полям/свойствам класса без диалога Roslyn (обход для Generate constructor from members).</summary>
public static class GenerateConstructor
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

    /// <summary>По позиции на класс генерирует конструктор с параметрами по выбранным полям/свойствам и присвоениями. Опционально: member_names, insert_into_file.</summary>
    public static async Task<string> GenerateConstructorFromMembersAsync(
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

            var propertyNames = typeSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsIndexer)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var members = new List<(string type, string memberName, string paramName)>();
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;
                if (memberSet != null && !memberSet.Contains(member.Name))
                    continue;

                switch (member)
                {
                    case IFieldSymbol field when field.IsConst == false && field.IsStatic == false:
                        if (IsBackingFieldForProperty(field.Name, propertyNames))
                            continue;
                        var p1 = ToCamelCase(field.Name);
                        var type1 = style.FormatTypeName(field.Type.ToDisplayString(TypeFormat));
                        members.Add((type1, field.Name, p1));
                        break;
                    case IPropertySymbol prop when prop.IsIndexer == false && !prop.IsReadOnly && prop.SetMethod != null:
                        var p2 = ToCamelCase(prop.Name);
                        var type2 = style.FormatTypeName(prop.Type.ToDisplayString(TypeFormat));
                        members.Add((type2, prop.Name, p2));
                        break;
                }
            }

            if (members.Count == 0)
                return "Error: no instance fields or writable properties to include. Add member_names or ensure the class has instance fields/properties with setter.";

            var indent = "\t\t";
            var ctorParams = string.Join(", ", members.Select(m => $"{m.type} {m.paramName}"));
            var assignments = string.Join(Environment.NewLine + indent, members.Select(m => $"{m.memberName} = {m.paramName};"));
            var ctorText = $"public {typeSymbol.Name}({ctorParams})" + Environment.NewLine + indent + "{" + Environment.NewLine + indent + assignments + Environment.NewLine + indent + "}";

            if (insertIntoFile && classDeclaration != null)
            {
                var (closeBrace, indentBeforeBrace) = GetClassCloseBraceAndIndent(classDeclaration, root);
                if (closeBrace != null)
                {
                    var insertIndent = indentBeforeBrace ?? style.IndentString;
                    var toInsert = Environment.NewLine + insertIndent + ctorText.Replace(indent, insertIndent) + Environment.NewLine + insertIndent;
                    var text = root.GetText();
                    var change = new TextChange(new TextSpan(closeBrace.Value.Span.Start, 0), toInsert);
                    var newText = text.WithChanges(change);
                    var newSolution = solution.WithDocumentText(document.Id, newText);
                    var applied = newSolution.Workspace.TryApplyChanges(newSolution);
                    if (applied)
                    {
                        newSolution.Workspace.Dispose();
                        return $"# Constructor inserted into {filePath}\n# Parameters: {members.Count}\n\n{ctorText}";
                    }
                }
            }

            solution?.Workspace.Dispose();
            return $"# Generated constructor for {typeSymbol.Name}\n# Paste into the class body. (Use insert_into_file: true to insert automatically.)\n\n{ctorText}";
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

    /// <summary>Поле считается backing для свойства, если есть свойство с тем же именем без ведущего '_' (например _source → Source).</summary>
    private static bool IsBackingFieldForProperty(string fieldName, HashSet<string> propertyNames)
    {
        if (string.IsNullOrEmpty(fieldName) || fieldName[0] != '_')
            return false;
        var withoutUnderscore = fieldName[1..];
        if (withoutUnderscore.Length == 0)
            return false;
        var propertyName = char.ToUpperInvariant(withoutUnderscore[0]) + withoutUnderscore[1..];
        return propertyNames.Contains(propertyName);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return name.ToLowerInvariant();
        if (name[0] == '_')
            name = name.Length > 1 ? name[1..] : name;
        return char.ToLowerInvariant(name[0]) + name[1..];
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
