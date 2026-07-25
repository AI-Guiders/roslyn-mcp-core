#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Agent parameter tip inside a call: overload list + XML docs (VS signature help, text).
/// Uses semantic model — <c>SignatureHelpService</c> is internal in Features.
/// </summary>
public static class GetSignatureHelp
{
    public const string Kind = "roslyn.get_signature_help";

    public static async Task<string> GetSignatureHelpAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string? sourceText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionOrProjectPath) || !File.Exists(solutionOrProjectPath))
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");
        if (string.IsNullOrWhiteSpace(filePath))
            return ToolStepJson.Fail(Kind, "file_path is required");
        if (line < 1 || column < 1)
            return ToolStepJson.Fail(Kind, "line/column must be 1-based >= 1");

        try
        {
            return await MsBuildWorkspaceHost.RunAsync(
                    solutionOrProjectPath,
                    async (_, solution, ct) =>
                    {
                        var doc = FindDocument(solution, filePath);
                        if (doc is null)
                            return ToolStepJson.Fail(Kind, $"document not in workspace: {filePath}");

                        if (!string.IsNullOrEmpty(sourceText))
                            doc = doc.WithText(SourceText.From(sourceText));

                        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
                        if (!TryPosition(text, line, column, out var position, out var posError))
                            return ToolStepJson.Fail(Kind, posError!);

                        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                        if (root is null || model is null)
                            return ToolStepJson.Fail(Kind, "syntax/semantic model unavailable");

                        var (invocation, argumentList, activeParameter) = FindCall(root, position);
                        if (invocation is null || argumentList is null)
                        {
                            return ToolStepJson.Ok(Kind, "no call at position", new
                            {
                                schema = "ide_signature_help/v0",
                                file = Path.GetFullPath(filePath),
                                line,
                                column,
                                active_signature = 0,
                                active_parameter = 0,
                                overloads = Array.Empty<object>()
                            });
                        }

                        var info = model.GetSymbolInfo(invocation, ct);
                        var methods = info.Symbol is IMethodSymbol one
                            ? new[] { one }
                            : info.CandidateSymbols.OfType<IMethodSymbol>().ToArray();

                        if (methods.Length == 0 && invocation is InvocationExpressionSyntax inv)
                        {
                            var exprInfo = model.GetSymbolInfo(inv.Expression, ct);
                            methods = exprInfo.Symbol is IMethodSymbol m
                                ? [m]
                                : exprInfo.CandidateSymbols.OfType<IMethodSymbol>().ToArray();
                        }

                        // Also object creation
                        if (methods.Length == 0 && invocation is ObjectCreationExpressionSyntax oc)
                        {
                            var ctorInfo = model.GetSymbolInfo(oc, ct);
                            methods = ctorInfo.Symbol is IMethodSymbol c
                                ? [c]
                                : ctorInfo.CandidateSymbols.OfType<IMethodSymbol>().ToArray();
                        }

                        var overloads = methods
                            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                            .Take(24)
                            .Select(m => new
                            {
                                label = m.ToDisplayString(DisplayFormat),
                                documentation = XmlDocRender.FromSymbol(m),
                                parameters = m.Parameters.Select(p => new
                                {
                                    name = p.Name,
                                    label = p.ToDisplayString(ParameterFormat),
                                    documentation = XmlDocRender.FromSymbol(p)
                                }).ToArray()
                            })
                            .ToArray();

                        var activeSig = 0;
                        if (info.Symbol is IMethodSymbol chosen)
                        {
                            for (var i = 0; i < methods.Length; i++)
                            {
                                if (SymbolEqualityComparer.Default.Equals(methods[i], chosen))
                                {
                                    activeSig = i;
                                    break;
                                }
                            }
                        }

                        return ToolStepJson.Ok(Kind, $"overloads={overloads.Length} active_param={activeParameter}", new
                        {
                            schema = "ide_signature_help/v0",
                            file = Path.GetFullPath(filePath),
                            line,
                            column,
                            active_signature = activeSig,
                            active_parameter = activeParameter,
                            overloads
                        });
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ToolStepJson.Fail(Kind, ex.Message);
        }
    }

    static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    static readonly SymbolDisplayFormat ParameterFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    static (SyntaxNode? Call, ArgumentListSyntax? Args, int ActiveParameter) FindCall(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv when inv.ArgumentList is { } args:
                    return (inv, args, ActiveParamIndex(args, position));
                case ObjectCreationExpressionSyntax oc when oc.ArgumentList is { } args:
                    return (oc, args, ActiveParamIndex(args, position));
                case ElementAccessExpressionSyntax ea when ea.ArgumentList is { } bracket:
                    // indexer — treat as call-like via semantic on ElementAccess
                    return (ea, null, 0);
            }
        }

        return (null, null, 0);
    }

    static int ActiveParamIndex(ArgumentListSyntax args, int position)
    {
        if (args.Arguments.Count == 0)
            return 0;

        for (var i = 0; i < args.Arguments.Count; i++)
        {
            var arg = args.Arguments[i];
            if (position <= arg.Span.End)
                return i;
            if (i < args.Arguments.SeparatorCount)
            {
                var sep = args.Arguments.GetSeparator(i);
                if (position <= sep.Span.End)
                    return i + 1;
            }
        }

        return Math.Max(0, args.Arguments.Count - 1);
    }

    static Document? FindDocument(Solution solution, string filePath)
    {
        var full = Path.GetFullPath(filePath.Trim());
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                d.FilePath is { } fp
                && string.Equals(Path.GetFullPath(fp), full, StringComparison.OrdinalIgnoreCase));
    }

    static bool TryPosition(SourceText text, int line, int column, out int position, out string? error)
    {
        position = 0;
        error = null;
        var lines = text.Lines;
        if (line < 1 || line > lines.Count)
        {
            error = $"line {line} out of range (1..{lines.Count})";
            return false;
        }

        var lineInfo = lines[line - 1];
        var col = column - 1;
        if (col < 0)
        {
            error = "column must be >= 1";
            return false;
        }

        var len = lineInfo.Span.Length;
        position = len == 0 ? lineInfo.Start : lineInfo.Start + Math.Min(col, len);
        return true;
    }
}
