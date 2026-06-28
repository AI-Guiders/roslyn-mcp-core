using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcp.ServiceLayer;

/// <summary>Генерация абстрактного базового класса по классу без диалогов Roslyn: выбранные public-члены становятся abstract в новом классе.</summary>
public static class GenerateBaseClass
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

    /// <summary>По позиции (класс в файле) генерирует абстрактный базовый класс с выбранными членами как abstract. Опционально: имя базового класса, путь к файлу, фильтр по именам членов.</summary>
    public static async Task<string> GenerateBaseClassFromClassAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string? baseClassName = null,
        string? outputFilePath = null,
        IReadOnlyList<string>? memberNames = null,
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
            while (node != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sym = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (sym is INamedTypeSymbol named && named.TypeKind == TypeKind.Class)
                {
                    typeSymbol = named;
                    break;
                }
                node = node.Parent;
            }

            if (typeSymbol is null)
                return $"Error: no class at {filePath}:{line}:{column}. Position the cursor on the class name or inside the class body. (Struct not supported for base class.)";

            var style = EditorConfigStyle.GetOptionsForDirectory(Path.GetDirectoryName(document.FilePath) ?? "");

            var ns = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null
                : typeSymbol.ContainingNamespace?.ToDisplayString();
            var name = baseClassName?.Trim();
            if (string.IsNullOrEmpty(name))
                name = typeSymbol.Name + "Base";

            var memberSet = memberNames != null && memberNames.Count > 0
                ? new HashSet<string>(memberNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase)
                : null;

            var members = new List<string>();
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (memberSet != null && !memberSet.Contains(member.Name))
                    continue;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                        members.Add(FormatAbstractMethod(method, style));
                        break;
                    case IPropertySymbol prop when prop.IsIndexer == false:
                        members.Add(FormatAbstractProperty(prop, style));
                        break;
                    case IEventSymbol evt:
                        members.Add(FormatAbstractEvent(evt, style));
                        break;
                }
            }

            if (members.Count == 0)
                return $"Error: no public instance methods/properties/events to extract for {typeSymbol.Name}. Specify member_names or ensure the class has public members.";

            var code = BuildAbstractClassFile(ns, name, members);

            if (!string.IsNullOrWhiteSpace(outputFilePath))
            {
                var outPath = Path.GetFullPath(outputFilePath.Trim());
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(outPath, code, cancellationToken).ConfigureAwait(false);
                return $"# Abstract base class generated: {name}\n# Written to: {outPath}\n# Next: add to {typeSymbol.Name} `: {name}` and add override to the extracted members.\n\n{code}";
            }

            return $"# Abstract base class generated: {name}\n# (pass output_file_path to write to disk)\n# Next: add to {typeSymbol.Name} `: {name}` and add override to the extracted members.\n\n{code}";
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

    private static string FormatAbstractMethod(IMethodSymbol method, EditorStyleOptions style)
    {
        var ret = style.FormatTypeName(method.ReturnType.ToDisplayString(TypeFormat));
        var ps = string.Join(", ", method.Parameters.Select(p => $"{style.FormatTypeName(p.Type.ToDisplayString(TypeFormat))} {p.Name}"));
        return $"\tprotected abstract {ret} {method.Name}({ps});";
    }

    private static string FormatAbstractProperty(IPropertySymbol prop, EditorStyleOptions style)
    {
        var type = style.FormatTypeName(prop.Type.ToDisplayString(TypeFormat));
        var getSet = prop.IsReadOnly ? "get;" : "get; set;";
        return $"\tprotected abstract {type} {prop.Name} {{ {getSet} }}";
    }

    private static string FormatAbstractEvent(IEventSymbol evt, EditorStyleOptions style)
    {
        var type = style.FormatTypeName(evt.Type.ToDisplayString(TypeFormat));
        return $"\tprotected abstract event {type} {evt.Name};";
    }

    private static string BuildAbstractClassFile(string? ns, string baseClassName, List<string> members)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").AppendLine(ns).AppendLine("{");
        }
        sb.Append("\tpublic abstract class ").AppendLine(baseClassName);
        sb.AppendLine("\t{");
        sb.AppendLine(string.Join(Environment.NewLine, members));
        sb.AppendLine("\t}");
        if (!string.IsNullOrEmpty(ns))
            sb.AppendLine("}");
        return sb.ToString();
    }
}
