#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Agent IntelliSense (Ctrl+Space): Roslyn <see cref="CompletionService"/> + rendered XML/docs tip.
/// </summary>
public static class GetCompletions
{
    public const string Kind = "roslyn.get_completions";

    public static async Task<string> GetCompletionsAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string? prefix,
        int max,
        string? sourceText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionOrProjectPath) || !File.Exists(solutionOrProjectPath))
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");
        if (string.IsNullOrWhiteSpace(filePath))
            return ToolStepJson.Fail(Kind, "file_path is required");
        if (line < 1 || column < 1)
            return ToolStepJson.Fail(Kind, "line/column must be 1-based >= 1");
        if (max <= 0) max = 40;
        if (max > 80) max = 80;

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

                        var service = CompletionService.GetService(doc);
                        if (service is null)
                            return ToolStepJson.Fail(Kind, "CompletionService unavailable for this document");

                        var results = await service.GetCompletionsAsync(doc, position, cancellationToken: ct)
                            .ConfigureAwait(false);
                        if (results is null || results.ItemsList.Count == 0)
                        {
                            return ToolStepJson.Ok(Kind, "no completions", new
                            {
                                schema = "ide_completions/v0",
                                file = Path.GetFullPath(filePath),
                                line,
                                column,
                                prefix = prefix ?? InferPrefix(text, position),
                                is_incomplete = false,
                                shown = 0,
                                items = Array.Empty<object>()
                            });
                        }

                        var filter = prefix ?? InferPrefix(text, position);
                        IEnumerable<CompletionItem> filtered = results.ItemsList;
                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            filtered = results.ItemsList.Where(i =>
                                i.DisplayText.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                                || (i.FilterText?.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                                || (i.SortText?.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ?? false));
                        }

                        var semantic = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                        var items = new List<object>();
                        var truncated = false;
                        foreach (var item in filtered)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (items.Count >= max)
                            {
                                truncated = true;
                                break;
                            }

                            items.Add(await BuildItemAsync(service, doc, item, semantic, position, ct)
                                .ConfigureAwait(false));
                        }

                        return ToolStepJson.Ok(Kind, $"shown={items.Count} prefix={filter ?? ""}", new
                        {
                            schema = "ide_completions/v0",
                            file = Path.GetFullPath(filePath),
                            line,
                            column,
                            prefix = filter,
                            is_incomplete = truncated,
                            shown = items.Count,
                            items
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

    static async Task<object> BuildItemAsync(
        CompletionService service,
        Document document,
        CompletionItem item,
        SemanticModel? semantic,
        int position,
        CancellationToken ct)
    {
        string? rendered = null;
        object? documentation = null;
        try
        {
            var description = await service.GetDescriptionAsync(document, item, ct).ConfigureAwait(false);
            if (description is not null)
            {
                rendered = XmlDocRender.FlattenTagged(
                    description.TaggedParts.Select(p => (p.Tag.ToString(), p.Text)));
            }
        }
        catch
        {
            // description optional
        }

        var symbol = TryResolveSymbol(semantic, position, item.DisplayText);
        documentation = XmlDocRender.FromSymbol(symbol);
        if (documentation is null && rendered is { Length: > 0 })
            documentation = new { summary = rendered };

        var insert = item.DisplayText;
        if (item.Properties.TryGetValue("InsertionText", out var insertion) && !string.IsNullOrEmpty(insertion))
            insert = insertion;

        return new
        {
            label = item.DisplayText,
            insert,
            kind = MapKind(item.Tags),
            detail = item.InlineDescription is { Length: > 0 } ? item.InlineDescription : rendered,
            documentation,
            tags = item.Tags.Length == 0 ? null : item.Tags.ToArray()
        };
    }

    static ISymbol? TryResolveSymbol(SemanticModel? model, int position, string displayText)
    {
        if (model is null || string.IsNullOrWhiteSpace(displayText))
            return null;
        var name = displayText;
        var lt = name.IndexOf('<');
        if (lt > 0) name = name[..lt];
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];

        try
        {
            var symbols = model.LookupSymbols(position, name: name);
            return symbols.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.Ordinal)
                || string.Equals(s.Name, displayText, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    static string MapKind(ImmutableArray<string> tags)
    {
        foreach (var t in tags)
        {
            if (t is "Method" or "ExtensionMethod") return "method";
            if (t is "Property") return "property";
            if (t is "Field" or "Local" or "Parameter" or "RangeVariable") return "field";
            if (t is "Class" or "Structure" or "Interface" or "Enum" or "Delegate" or "Record") return "type";
            if (t is "Namespace") return "namespace";
            if (t is "Keyword") return "keyword";
            if (t is "Snippet") return "snippet";
            if (t is "Event") return "event";
        }

        return tags.Length > 0 ? tags[0].ToLowerInvariant() : "text";
    }

    static string? InferPrefix(SourceText text, int position)
    {
        if (position <= 0 || position > text.Length) return null;
        var i = position - 1;
        while (i >= 0)
        {
            var ch = text[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '@'))
                break;
            i--;
        }

        var start = i + 1;
        if (start >= position) return null;
        return text.ToString(TextSpan.FromBounds(start, position));
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
