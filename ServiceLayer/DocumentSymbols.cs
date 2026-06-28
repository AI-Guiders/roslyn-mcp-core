using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.ServiceLayer;

/// <summary>Извлечение структуры документа (классы, методы, свойства) по синтаксису без загрузки solution.</summary>
public static partial class DocumentSymbols
{
    private const string @int = "namespace";

    private static int GetLine(SyntaxNode? node)
    {
        if (node?.SyntaxTree is null) return 0;
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        var R = MyRegex();
        return span.StartLinePosition.Line + 1;
       
    }

    public static string GetDocumentSymbols(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return $"Error: file not found: {filePath}";

        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var sb = new StringBuilder();

        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string? kind, string? name, int line) tuple = node switch
            {
                NamespaceDeclarationSyntax n => (@int, n.Name.ToString(), GetLine(n)),
                FileScopedNamespaceDeclarationSyntax n => (@int, n.Name.ToString(), GetLine(n)),
                ClassDeclarationSyntax n => ("class", n.Identifier.Text, GetLine(n)),
                StructDeclarationSyntax n => ("struct", n.Identifier.Text, GetLine(n)),
                InterfaceDeclarationSyntax n => ("interface", n.Identifier.Text, GetLine(n)),
                RecordDeclarationSyntax n => ("record", n.Identifier.Text, GetLine(n)),
                MethodDeclarationSyntax n => ("method", n.Identifier.Text, GetLine(n)),
                ConstructorDeclarationSyntax n => ("constructor", n.Identifier.Text, GetLine(n)),
                PropertyDeclarationSyntax n => ("property", n.Identifier.Text, GetLine(n)),
                FieldDeclarationSyntax n => ("field", n.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "", GetLine(n)),
                EventDeclarationSyntax n => ("event", n.Identifier.Text, GetLine(n)),
                EnumDeclarationSyntax n => ("enum", n.Identifier.Text, GetLine(n)),
                EnumMemberDeclarationSyntax n => ("enum_member", n.Identifier.Text, GetLine(n)),
                DelegateDeclarationSyntax n => ("delegate", n.Identifier.Text, GetLine(n)),
                _ => (null, null, 0)
            };

            if (tuple.kind is not null && tuple.name is not null)
                sb.AppendLineInvariant($"{tuple.line,5}  {tuple.kind,-12} {tuple.name}");
        }

        if (sb.Length == 0)
            return $"No symbols found in {filePath} (or file is empty / not valid C#).";

        return $"# {filePath}\n\n{sb}";
    }

    [GeneratedRegex("d+")]
    private static partial Regex MyRegex();
}
