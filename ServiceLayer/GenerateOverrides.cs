using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>Генерация override-членов по классу: виртуальные/абстрактные члены базового типа без диалога Roslyn.</summary>
public static class GenerateOverrides
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

    /// <summary>По позиции (класс в файле) генерирует override-заглушки для виртуальных/абстрактных членов базового типа. Опционально: фильтр member_names, вставка в файл.</summary>
    public static async Task<string> GenerateOverridesAsync(
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

            var baseType = typeSymbol.BaseType;
            if (baseType is null || baseType.SpecialType == SpecialType.System_Object)
                return "Error: class has no base class (other than Object). Nothing to override.";

            var alreadyOverridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var m in typeSymbol.GetMembers())
            {
                switch (m)
                {
                    case IMethodSymbol method when method.OverriddenMethod != null:
                        alreadyOverridden.Add(method.OverriddenMethod);
                        break;
                    case IPropertySymbol prop when prop.OverriddenProperty != null:
                        alreadyOverridden.Add(prop.OverriddenProperty);
                        break;
                    case IEventSymbol evt when evt.OverriddenEvent != null:
                        alreadyOverridden.Add(evt.OverriddenEvent);
                        break;
                }
            }

            var memberSet = memberNames != null && memberNames.Count > 0
                ? new HashSet<string>(memberNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase)
                : null;

            var overrides = new List<string>();
            for (var b = baseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var member in b.GetMembers())
                {
                    if (member.IsImplicitlyDeclared)
                        continue;
                    if (alreadyOverridden.Contains(member))
                        continue;
                    if (memberSet != null && !memberSet.Contains(member.Name))
                        continue;

                    switch (member)
                    {
                        case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary && (method.IsVirtual || method.IsOverride || method.IsAbstract):
                            overrides.Add(FormatOverrideMethod(method, style));
                            alreadyOverridden.Add(method);
                            break;
                        case IPropertySymbol prop when !prop.IsIndexer && (prop.IsVirtual || prop.IsOverride || prop.IsAbstract):
                            overrides.Add(FormatOverrideProperty(prop, style));
                            alreadyOverridden.Add(prop);
                            break;
                        case IEventSymbol evt when evt.IsVirtual || evt.IsOverride || evt.IsAbstract:
                            overrides.Add(FormatOverrideEvent(evt, style));
                            alreadyOverridden.Add(evt);
                            break;
                    }
                }
            }

            if (overrides.Count == 0)
                return "Error: no overridable members found in base type(s), or all are already overridden. Use member_names to select specific base members, or ensure the base class has virtual/abstract members.";

            var indent = "\t\t";
            var block = string.Join(Environment.NewLine + indent, overrides);

            if (insertIntoFile && classDeclaration != null)
            {
                var (closeBrace, indentBeforeBrace) = GetClassCloseBraceAndIndent(classDeclaration, root);
                if (closeBrace != null)
                {
                    var insertIndent = indentBeforeBrace ?? style.IndentString;
                    var toInsert = Environment.NewLine + insertIndent + string.Join(Environment.NewLine + insertIndent, overrides) + Environment.NewLine + insertIndent;
                    var text = root.GetText();
                    var change = new TextChange(new TextSpan(closeBrace.Value.Span.Start, 0), toInsert);
                    var newText = text.WithChanges(change);
                    var newSolution = solution.WithDocumentText(document.Id, newText);
                    var applied = newSolution.Workspace.TryApplyChanges(newSolution);
                    if (applied)
                    {
                        newSolution.Workspace.Dispose();
                        return $"# Overrides inserted into {filePath}\n# Members: {overrides.Count}\n\n{block}";
                    }
                }
            }

            solution?.Workspace.Dispose();
            return $"# Generated overrides for {typeSymbol.Name} (base: {baseType.Name})\n# Paste into the class body. (Use insert_into_file: true to insert automatically.)\n\n{block}";
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

    private static string FormatOverrideMethod(IMethodSymbol method, EditorStyleOptions style)
    {
        var ret = style.FormatTypeName(method.ReturnType.ToDisplayString(TypeFormat));
        var ps = string.Join(", ", method.Parameters.Select(p => $"{style.FormatTypeName(p.Type.ToDisplayString(TypeFormat))} {p.Name}"));
        var body = method.ReturnsVoid
            ? "throw new NotImplementedException();"
            : "return default;";
        return $"public override {ret} {method.Name}({ps}) => {body}";
    }

    private static string FormatOverrideProperty(IPropertySymbol prop, EditorStyleOptions style)
    {
        var type = style.FormatTypeName(prop.Type.ToDisplayString(TypeFormat));
        if (prop.IsReadOnly)
            return $"public override {type} {prop.Name} => throw new NotImplementedException();";
        return $"public override {type} {prop.Name} {{ get => throw new NotImplementedException(); set => throw new NotImplementedException(); }}";
    }

    private static string FormatOverrideEvent(IEventSymbol evt, EditorStyleOptions style)
    {
        var type = style.FormatTypeName(evt.Type.ToDisplayString(TypeFormat));
        return $"public override event {type} {evt.Name} {{ add => throw new NotImplementedException(); remove => throw new NotImplementedException(); }}";
    }
}
