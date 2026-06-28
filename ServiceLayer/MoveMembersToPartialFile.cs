using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcp.ServiceLayer;

/// <summary>Перенос выбранных членов типа в новый файл с объявлением <c>partial</c> того же типа; в исходном файле добавляется <c>partial</c> при необходимости.</summary>
public static class MoveMembersToPartialFile
{
    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <summary>
    /// Перенос членов по именам в новый .cs файл (тот же тип, partial). Позиция — на имени типа или внутри тела типа.
    /// Конструктор: укажи имя типа или <c>.ctor</c>. Индексатор: <c>this</c>. Перегрузки методов с одним именем переносятся все.
    /// </summary>
    public static async Task<string> MoveAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        IReadOnlyList<string> memberNames,
        string outputFilePath,
        bool apply,
        bool addDependentUpon = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return $"Error: solution/project not found: {solutionOrProjectPath}";
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";
        if (memberNames is null || memberNames.Count == 0)
            return "Error: member_names must be a non-empty array.";
        var trimmedNames = memberNames.Select(static n => n.Trim()).Where(static n => n.Length > 0).ToArray();
        if (trimmedNames.Length == 0)
            return "Error: member_names must contain at least one non-empty name.";

        var outPath = Path.GetFullPath(outputFilePath.Trim());
        if (apply && File.Exists(outPath))
            return $"Error: output file already exists (delete or choose another path): {outPath}";

        var targetPath = NormalizePath(filePath);
        MSBuildWorkspace? workspace = null;
        try
        {
            workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
            var solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, solutionOrProjectPath, cancellationToken).ConfigureAwait(false);
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
            TypeDeclarationSyntax? typeDecl = null;
            INamedTypeSymbol? typeSymbol = null;
            while (node is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is TypeDeclarationSyntax tds)
                {
                    var sym = semanticModel.GetDeclaredSymbol(tds, cancellationToken);
                    if (sym is INamedTypeSymbol named && named.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface)
                    {
                        typeDecl = tds;
                        typeSymbol = named;
                        break;
                    }
                }
                node = node.Parent;
            }

            if (typeDecl is null || typeSymbol is null)
                return $"Error: no class, struct, or interface at {filePath}:{line}:{column}. Put the cursor on the type name or inside its body.";

            if (typeSymbol.TypeKind == TypeKind.Interface)
                return "Error: moving members out of an interface is not supported.";

            var nameSet = new HashSet<string>(trimmedNames, StringComparer.OrdinalIgnoreCase);
            var collectError = TryCollectMembersToMove(
                typeDecl, semanticModel, typeSymbol, nameSet, cancellationToken, out var toMove);
            if (collectError is not null)
                return collectError;

            if (toMove.Count == 0)
            {
                var available = string.Join(", ", ListMemberNamesForHint(typeDecl, semanticModel, cancellationToken).Take(40));
                var hint = string.IsNullOrEmpty(available) ? "" : $"\n# Hint (first members in type): {available}";
                return $"Error: no members matched member_names for type {typeSymbol.Name}. Check spelling (constructors: type name or .ctor; indexer: this).{hint}";
            }

            var compilationUnit = root is CompilationUnitSyntax cu ? cu : null;
            if (compilationUnit is null)
                return "Error: unexpected root (expected compilation unit).";

            var fileScopedNs = IsDeclaredInFileScopedNamespace(typeDecl);
            var newTypePart = EnsurePartialKeyword(CloneTypeShellWithMembers(typeDecl, toMove));
            var remainingAfterRemove = typeDecl.RemoveNodes(toMove, SyntaxRemoveOptions.KeepEndOfLine) ?? typeDecl;
            var remainingType = EnsurePartialKeyword(remainingAfterRemove);

            var newFileText = BuildPartialPartFileText(compilationUnit, newTypePart, typeSymbol, fileScopedNs);
            var modifiedRoot = root.ReplaceNode(typeDecl, remainingType);
            var modifiedText = modifiedRoot.GetText();

            var sb = new StringBuilder();
            sb.AppendLineInvariant($"# Type: {typeSymbol.ToDisplayString()}");
            sb.AppendLineInvariant($"# Members moved: {toMove.Count} ({string.Join(", ", toMove.Select(GetMemberLabel))})");
            sb.AppendLineInvariant($"# Output: {outPath}");
            sb.AppendLineInvariant($"# Source updated: {filePath}");
            sb.AppendLine(apply ? "# apply: true — writing files." : "# apply: false — preview only. Pass apply: true to write.");
            sb.AppendLine();

            if (!apply)
            {
                sb.AppendLine("## New file content");
                sb.AppendLine();
                sb.AppendLine(newFileText);
                sb.AppendLine();
                sb.AppendLine("## Modified source (excerpt, first 80 lines)");
                sb.AppendLine();
                var fullModified = modifiedText.ToString();
                var lineArr = fullModified.Split(['\n', '\r'], StringSplitOptions.None);
                var take = Math.Min(80, lineArr.Length);
                for (var i = 0; i < take; i++)
                    sb.AppendLine(lineArr[i]);
                if (lineArr.Length > 80)
                    sb.AppendLine("...");
                return sb.ToString();
            }

            var project = document.Project;
            if (project.FilePath is not null)
            {
                var projDir = Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
                var outDir = Path.GetDirectoryName(outPath);
                if (projDir is not null && outDir is not null)
                {
                    var fullProj = Path.GetFullPath(projDir);
                    var fullOut = Path.GetFullPath(outDir);
                    if (!fullOut.StartsWith(fullProj, Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        sb.AppendLineInvariant($"# Warning: output path is outside project directory ({fullProj}). SDK glob may not include the file; prefer a path under the project.");
                }
            }

            var solutionWithOriginal = solution.WithDocumentText(document.Id, modifiedText);
            var projectAfter = solutionWithOriginal.GetDocument(document.Id)?.Project
                ?? throw new InvalidOperationException("Document missing after update.");
            var folders = GetRelativeFolderSegments(projectAfter.FilePath, outPath);
            var fileName = Path.GetFileName(outPath);
            var withNewDoc = projectAfter.AddDocument(fileName, newFileText, folders, outPath);
            var finalSolution = withNewDoc.Project.Solution;

            if (!workspace.TryApplyChanges(finalSolution))
                return "Error: TryApplyChanges returned false (files may be read-only or path invalid).";

            sb.AppendLine("Applied: source file updated and new partial file created.");

            var csprojPathApply = withNewDoc.Project.FilePath;
            if (!string.IsNullOrEmpty(csprojPathApply))
            {
                var projDirApply = Path.GetDirectoryName(Path.GetFullPath(csprojPathApply));
                if (projDirApply is not null)
                {
                    var childRelApply = Path.GetRelativePath(projDirApply, outPath).Replace('/', Path.DirectorySeparatorChar);
                    var stripMsg = DependentUponCsproj.TryRemoveRedundantSdkCompileInclude(csprojPathApply, childRelApply);
                    sb.AppendLineInvariant($"# Sdk Compile: {stripMsg}");

                    if (addDependentUpon)
                    {
                        var depVal = DependentUponCsproj.ComputeDependentUponValue(projDirApply, outPath, targetPath);
                        var depMsg = DependentUponCsproj.AddOrUpdateDependentUpon(csprojPathApply, childRelApply, depVal, dryRun: false);
                        sb.AppendLineInvariant($"# DependentUpon: {depMsg}");
                    }
                }
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slnx") || ex.Message.Contains("Slnx"))
        {
            return "Error: .slnx format is not supported. Use .sln or .csproj.";
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private static string GetMemberLabel(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier.ValueText,
        ConstructorDeclarationSyntax cd => cd.Identifier.ValueText.Length > 0 ? cd.Identifier.ValueText : ".ctor",
        DestructorDeclarationSyntax => "Finalize",
        PropertyDeclarationSyntax pd => pd.Identifier.ValueText,
        IndexerDeclarationSyntax => "this",
        FieldDeclarationSyntax fd => string.Join(", ", fd.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        EventDeclarationSyntax ed => ed.Identifier.ValueText,
        EventFieldDeclarationSyntax efd => string.Join(", ", efd.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        TypeDeclarationSyntax td => td.Identifier.ValueText,
        EnumDeclarationSyntax en => en.Identifier.ValueText,
        _ => m.Kind().ToString()
    };

    private static string[] GetRelativeFolderSegments(string? projectFilePath, string documentFullPath)
    {
        if (string.IsNullOrEmpty(projectFilePath))
            return [];
        var projDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var docDir = Path.GetDirectoryName(Path.GetFullPath(documentFullPath));
        if (projDir is null || docDir is null)
            return [];
        var rel = Path.GetRelativePath(projDir, docDir);
        if (rel is "." or { Length: 0 })
            return [];
        return rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> ListMemberNamesForHint(TypeDeclarationSyntax typeDecl, SemanticModel model, CancellationToken ct)
    {
        foreach (var m in typeDecl.Members)
        {
            ct.ThrowIfCancellationRequested();
            switch (m)
            {
                case FieldDeclarationSyntax fd:
                    foreach (var v in fd.Declaration.Variables)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (model.GetDeclaredSymbol(v, ct) is IFieldSymbol fs)
                            yield return fs.Name;
                    }
                    break;
                case EventFieldDeclarationSyntax efd:
                    foreach (var v in efd.Declaration.Variables)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (model.GetDeclaredSymbol(v, ct) is IEventSymbol es)
                            yield return es.Name;
                    }
                    break;
                default:
                    var sym = model.GetDeclaredSymbol(m, ct);
                    if (sym is null)
                        continue;
                    yield return sym.Name switch
                    {
                        ".ctor" => typeDecl.Identifier.ValueText,
                        _ when sym is IPropertySymbol { IsIndexer: true } => "this",
                        _ => sym.Name
                    };
                    break;
            }
        }
    }

    /// <summary>
    /// Поля и event-поля: символ у каждого <see cref="VariableDeclaratorSyntax"/>; для узла <see cref="FieldDeclarationSyntax"/> целиком
    /// <see cref="SemanticModel.GetDeclaredSymbol(Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax, CancellationToken)"/> часто даёт null (const и др.).
    /// </summary>
    private static string? TryCollectMembersToMove(
        TypeDeclarationSyntax typeDecl,
        SemanticModel semanticModel,
        INamedTypeSymbol typeSymbol,
        HashSet<string> nameSet,
        CancellationToken ct,
        out List<MemberDeclarationSyntax> toMove)
    {
        toMove = new List<MemberDeclarationSyntax>();
        foreach (var member in typeDecl.Members)
        {
            ct.ThrowIfCancellationRequested();
            switch (member)
            {
                case FieldDeclarationSyntax fd:
                {
                    var err = TryAddFieldOrEventFieldDeclaration(
                        fd, fd.Declaration.Variables, semanticModel, nameSet, typeSymbol.Name, toMove, ct);
                    if (err is not null)
                        return err;
                    break;
                }
                case EventFieldDeclarationSyntax efd:
                {
                    var err = TryAddFieldOrEventFieldDeclaration(
                        efd, efd.Declaration.Variables, semanticModel, nameSet, typeSymbol.Name, toMove, ct);
                    if (err is not null)
                        return err;
                    break;
                }
                default:
                {
                    var sym = semanticModel.GetDeclaredSymbol(member, ct);
                    if (sym is null)
                        continue;
                    if (ShouldMoveMember(sym, nameSet, typeSymbol.Name) && !toMove.Contains(member))
                        toMove.Add(member);
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Если в одном объявлении несколько переменных и в <paramref name="nameSet"/> попадает только часть имён — перенос невозможен без разбиения объявления.
    /// </summary>
    private static string? TryAddFieldOrEventFieldDeclaration(
        MemberDeclarationSyntax declarationNode,
        SeparatedSyntaxList<VariableDeclaratorSyntax> variables,
        SemanticModel semanticModel,
        HashSet<string> nameSet,
        string typeName,
        List<MemberDeclarationSyntax> toMove,
        CancellationToken ct)
    {
        if (variables.Count == 0)
            return null;

        var anyMatch = false;
        var anyResolvedNonMatch = false;
        foreach (var variable in variables)
        {
            ct.ThrowIfCancellationRequested();
            var sym = semanticModel.GetDeclaredSymbol(variable, ct);
            if (sym is null)
                continue;
            if (ShouldMoveMember(sym, nameSet, typeName))
                anyMatch = true;
            else
                anyResolvedNonMatch = true;
        }

        if (!anyMatch)
            return null;

        if (anyResolvedNonMatch)
        {
            var line = declarationNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return $"Error: field/event declaration at line {line} declares multiple variables; cannot move a subset in one step. Split into separate declarations or list all names in member_names.";
        }

        if (!toMove.Contains(declarationNode))
            toMove.Add(declarationNode);
        return null;
    }

    private static bool ShouldMoveMember(ISymbol sym, HashSet<string> names, string typeName)
    {
        return sym switch
        {
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor =>
                names.Contains(typeName) || names.Contains(".ctor"),
            IMethodSymbol m when m.MethodKind == MethodKind.Destructor =>
                names.Contains("Finalize") || names.Contains("~" + typeName),
            IPropertySymbol { IsIndexer: true } =>
                names.Contains("this"),
            IMethodSymbol m =>
                names.Contains(m.Name),
            IPropertySymbol p =>
                names.Contains(p.Name),
            IFieldSymbol f =>
                names.Contains(f.Name),
            IEventSymbol e =>
                names.Contains(e.Name),
            INamedTypeSymbol nt when nt.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum or TypeKind.Interface =>
                names.Contains(nt.Name),
            _ => false
        };
    }

    /// <summary>
    /// Добавляет <c>partial</c> если его ещё нет. Порядок: <c>partial</c> — последний модификатор перед keyword типа
    /// (<c>class</c>/<c>struct</c>/<c>record</c>/<c>interface</c>), иначе CS0267 (<c>public partial sealed class</c> неверно).
    /// </summary>
    private static TypeDeclarationSyntax EnsurePartialKeyword(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            return typeDecl;
        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        return typeDecl.WithModifiers(typeDecl.Modifiers.Add(partialToken));
    }

    private static TypeDeclarationSyntax CloneTypeShellWithMembers(TypeDeclarationSyntax original, IReadOnlyList<MemberDeclarationSyntax> members) =>
        original
            .WithMembers(SyntaxFactory.List(members))
            .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>())
            .WithLeadingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine));

    private static bool IsDeclaredInFileScopedNamespace(TypeDeclarationSyntax typeDecl)
    {
        for (var p = typeDecl.Parent; p != null; p = p.Parent)
        {
            if (p is FileScopedNamespaceDeclarationSyntax)
                return true;
            if (p is NamespaceDeclarationSyntax)
                return false;
        }
        return false;
    }

    private static string BuildPartialPartFileText(
        CompilationUnitSyntax originalCu,
        TypeDeclarationSyntax typePart,
        INamedTypeSymbol typeSymbol,
        bool fileScopedNamespace)
    {
        var globalNs = typeSymbol.ContainingNamespace is null or { IsGlobalNamespace: true };
        var nsDisplay = globalNs ? null : typeSymbol.ContainingNamespace!.ToDisplayString();
        var fileScoped = fileScopedNamespace && !globalNs;

        MemberDeclarationSyntax typeAsMember = typePart;
        MemberDeclarationSyntax wrapped;

        if (globalNs)
            wrapped = typeAsMember;
        else if (string.IsNullOrEmpty(nsDisplay))
            wrapped = typeAsMember;
        else if (fileScoped)
        {
            wrapped = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(nsDisplay))
                .WithMembers(SyntaxFactory.SingletonList(typeAsMember));
        }
        else
        {
            wrapped = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(nsDisplay!))
                .WithMembers(SyntaxFactory.SingletonList(typeAsMember))
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        var cu = SyntaxFactory.CompilationUnit()
            .WithExterns(originalCu.Externs)
            .WithUsings(originalCu.Usings);

        cu = globalNs || string.IsNullOrEmpty(nsDisplay)
            ? cu.AddMembers(typeAsMember)
            : cu.AddMembers(wrapped);

        return cu.NormalizeWhitespace(elasticTrivia: true).ToFullString();
    }
}
