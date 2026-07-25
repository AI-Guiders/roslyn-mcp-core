#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.ServiceLayer;

/// <summary>Render Roslyn XML doc comments as agent-readable summary/params (VS tip shape, text).</summary>
internal static class XmlDocRender
{
    const int MaxField = 600;

    public static object? FromSymbol(ISymbol? symbol)
    {
        if (symbol is null) return null;
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default);
        if (string.IsNullOrWhiteSpace(xml)) return null;
        return FromXml(xml);
    }

    public static object? FromXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var member = doc.Root?.Name.LocalName == "member" ? doc.Root : doc.Root?.Element("member") ?? doc.Root;
            if (member is null) return null;

            var summary = Flatten(member.Element("summary"));
            var returns = Flatten(member.Element("returns"));
            var remarks = Flatten(member.Element("remarks"));
            var parameters = member.Elements("param")
                .Select(p => new
                {
                    name = (string?)p.Attribute("name") ?? "",
                    docs = Cap(Flatten(p))
                })
                .Where(p => p.name.Length > 0)
                .ToArray();
            var typeParams = member.Elements("typeparam")
                .Select(p => new
                {
                    name = (string?)p.Attribute("name") ?? "",
                    docs = Cap(Flatten(p))
                })
                .Where(p => p.name.Length > 0)
                .ToArray();

            if (summary is null && returns is null && parameters.Length == 0 && typeParams.Length == 0 && remarks is null)
                return null;

            return new
            {
                summary = Cap(summary),
                @params = parameters.Length == 0 ? null : parameters,
                type_params = typeParams.Length == 0 ? null : typeParams,
                returns = Cap(returns),
                remarks = Cap(remarks)
            };
        }
        catch
        {
            var summary = TryExtractSummaryRegex(xml);
            return summary is null ? null : new { summary = Cap(summary) };
        }
    }

    public static string? FlattenTagged(IEnumerable<(string Tag, string Text)> parts)
    {
        var sb = new StringBuilder();
        foreach (var (tag, text) in parts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (tag is "LineBreak" or "SoftLineBreak")
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                continue;
            }

            sb.Append(text);
        }

        var s = CollapseWs(sb.ToString());
        return string.IsNullOrWhiteSpace(s) ? null : Cap(s);
    }

    static string? Flatten(XElement? el)
    {
        if (el is null) return null;
        var sb = new StringBuilder();
        FlattenNode(el, sb);
        var s = CollapseWs(sb.ToString());
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    static void FlattenNode(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText t:
                sb.Append(t.Value);
                break;
            case XElement e:
                if (e.Name.LocalName is "see" or "seealso")
                {
                    var cref = (string?)e.Attribute("cref") ?? (string?)e.Attribute("langword");
                    if (!string.IsNullOrWhiteSpace(cref))
                        sb.Append(SimplifyCref(cref));
                    else
                        sb.Append(e.Value);
                    break;
                }

                if (e.Name.LocalName is "paramref" or "typeparamref")
                {
                    var name = (string?)e.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(name))
                        sb.Append(name);
                    break;
                }

                if (e.Name.LocalName is "para" or "br")
                {
                    if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n')
                        sb.Append(' ');
                }

                foreach (var child in e.Nodes())
                    FlattenNode(child, sb);
                break;
        }
    }

    static string SimplifyCref(string cref)
    {
        var s = cref;
        if (s.Length > 2 && s[1] == ':')
            s = s[2..];
        var tick = s.IndexOf('`');
        if (tick >= 0) s = s[..tick];
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var last = s.LastIndexOf('.');
        return last >= 0 ? s[(last + 1)..] : s;
    }

    static string? TryExtractSummaryRegex(string xml)
    {
        var m = Regex.Match(xml, "<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return CollapseWs(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " "));
    }

    static string CollapseWs(string s) =>
        Regex.Replace(s.Replace('\r', ' ').Replace('\n', ' '), "\\s+", " ").Trim();

    static string? Cap(string? s)
    {
        if (s is null) return null;
        if (s.Length <= MaxField) return s;
        return s[..MaxField] + "…";
    }
}
