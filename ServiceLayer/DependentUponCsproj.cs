using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Запись <c>&lt;Compile Update="..."&gt;&lt;DependentUpon&gt;...&lt;/DependentUpon&gt;&lt;/Compile&gt;</c> в SDK-стиле .csproj
/// (та же семантика путей, что в обозревателе CascadeIDE по DependentUpon).
/// </summary>
public static class DependentUponCsproj
{
    /// <summary>
    /// Значение для DependentUpon: в той же папке — только имя файла; иначе — относительный путь от корня проекта.
    /// </summary>
    public static string ComputeDependentUponValue(string projectDirectory, string childFullPath, string parentFullPath)
    {
        var proj = Path.GetFullPath(projectDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var childDir = Path.GetDirectoryName(childFullPath);
        var parentDir = Path.GetDirectoryName(parentFullPath);
        if (childDir is not null && parentDir is not null &&
            string.Equals(Path.GetFullPath(childDir), Path.GetFullPath(parentDir), StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(parentFullPath);
        return Path.GetRelativePath(proj, Path.GetFullPath(parentFullPath))
            .Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Эвристика: для <c>Stem.Rest.cs</c> взять самый длинный существующий <c>Stem...cs</c> в той же папке (как <c>TryInferParentCsFileName</c> в CascadeIDE).
    /// </summary>
    public static string? TryInferParentCsFileName(string fileName, HashSet<string> fileNamesInSameDir)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(stem))
            return null;
        var parts = stem.Split('.');
        if (parts.Length < 2)
            return null;
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            var candidate = string.Join(".", parts.Take(i)) + ".cs";
            if (fileNamesInSameDir.Contains(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Добавляет или обновляет <c>Compile Update</c> с DependentUpon. Путь в Update — относительно .csproj.
    /// </summary>
    public static string AddOrUpdateDependentUpon(string csprojPath, string compileUpdateRelativePath, string dependentUponValue, bool dryRun)
    {
        if (!File.Exists(csprojPath))
            return $"Error: csproj not found: {csprojPath}";

        var updateNorm = NormalizeProjRelative(compileUpdateRelativePath);
        var depTrim = dependentUponValue.Trim().Replace('/', Path.DirectorySeparatorChar);

        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var rootNs = doc.Root!.Name.Namespace;
        var changed = false;
        var msg = ApplyMergeToDocument(doc, rootNs, updateNorm, depTrim, dryRun, ref changed);
        if (!dryRun && changed)
            SaveCsproj(csprojPath, doc);
        return msg;
    }

    private static void SaveCsproj(string csprojPath, XDocument doc)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, NewLineChars = "\r\n" };
        using var writer = XmlWriter.Create(csprojPath, settings);
        doc.Save(writer);
    }

    private static string NormalizeProjRelative(string rel)
    {
        var t = rel.Trim().Replace('/', Path.DirectorySeparatorChar);
        return t.TrimStart('.', Path.DirectorySeparatorChar);
    }

    private static bool PathsEqualProjRelative(string a, string b) =>
        string.Equals(NormalizeProjRelative(a), NormalizeProjRelative(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>MSBuildWorkspace</c> после <c>AddDocument</c> часто записывает <c>&lt;Compile Include="..." /&gt;</c>.
    /// В SDK-проектах с default compile items это дублирует glob и даёт NETSDK1022.
    /// Удаляет одиночный <c>Include</c> для указанного относительного пути, если корень проекта с атрибутом <c>Sdk</c>
    /// и <c>EnableDefaultCompileItems</c> не равен <c>false</c>.
    /// </summary>
    public static string TryRemoveRedundantSdkCompileInclude(string csprojPath, string compileIncludeRelativePath)
    {
        if (!File.Exists(csprojPath))
            return "skipped (csproj missing)";

        var targetNorm = NormalizeProjRelative(compileIncludeRelativePath);

        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null)
            return "skipped (invalid root)";

        if (root.Attribute("Sdk") is null)
            return "skipped (non-Sdk Project; no redundant glob duplicate expected)";

        foreach (var pg in doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            foreach (var p in pg.Elements())
            {
                if (p.Name.LocalName != "EnableDefaultCompileItems")
                    continue;
                if (string.Equals(p.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                    return "skipped (EnableDefaultCompileItems=false)";
            }
        }

        var removed = 0;
        foreach (var ig in doc.Descendants().Where(e => e.Name.LocalName == "ItemGroup").ToList())
        {
            foreach (var c in ig.Elements().Where(e => e.Name.LocalName == "Compile").ToList())
            {
                if (c.Attribute("Update") is not null)
                    continue;
                var inc = (string?)c.Attribute("Include");
                if (inc is null)
                    continue;
                if (inc.Contains(';', StringComparison.Ordinal))
                    continue;
                if (!PathsEqualProjRelative(inc.Trim(), targetNorm))
                    continue;
                c.Remove();
                removed++;
            }

            if (!ig.Elements().Any())
                ig.Remove();
        }

        if (removed == 0)
            return "no redundant Compile Include (already implicit or absent)";

        SaveCsproj(csprojPath, doc);
        return $"removed {removed} redundant Compile Include for '{targetNorm}'";
    }

    /// <summary>
    /// Несколько пар за одну загрузку/сохранение .csproj (для массового sync).
    /// </summary>
    public static string ApplyBatch(string csprojPath, IReadOnlyList<(string CompileUpdateRelative, string DependentUponValue)> items, bool dryRun, StringBuilder? logLines = null)
    {
        if (!File.Exists(csprojPath))
            return $"Error: csproj not found: {csprojPath}";
        if (items.Count == 0)
            return "# No Compile/DependentUpon pairs to apply.";

        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var rootNs = doc.Root!.Name.Namespace;
        var changed = false;
        var noops = 0;

        foreach (var (rel, dep) in items)
        {
            var updateNorm = NormalizeProjRelative(rel);
            var depTrim = dep.Trim().Replace('/', Path.DirectorySeparatorChar);
            var line = ApplyMergeToDocument(doc, rootNs, updateNorm, depTrim, dryRun, ref changed);
            if (line.StartsWith("# No change", StringComparison.Ordinal))
                noops++;
            logLines?.AppendLineInvariant($"  {line}");
        }

        if (!dryRun && changed)
            SaveCsproj(csprojPath, doc);

        return $"# Batch: items={items.Count}, no_change={noops}, modified={changed}, dry_run={dryRun}";
    }

    /// <summary>Одна пара: либо меняет <paramref name="doc"/> (если не dry_run и нужно), либо только описывает действие.</summary>
    private static string ApplyMergeToDocument(XDocument doc, XNamespace rootNs, string updateNorm, string depTrim, bool dryRun, ref bool changed)
    {
        XElement? FindCompileUpdate()
        {
            foreach (var ig in doc.Descendants().Where(e => e.Name.LocalName == "ItemGroup"))
            {
                foreach (var el in ig.Elements())
                {
                    if (el.Name.LocalName != "Compile")
                        continue;
                    var upd = (string?)el.Attribute("Update");
                    if (upd is not null && PathsEqualProjRelative(upd, updateNorm))
                        return el;
                }
            }
            return null;
        }

        var existing = FindCompileUpdate();
        if (existing is not null)
        {
            var depEl = existing.Elements().FirstOrDefault(e => e.Name.LocalName == "DependentUpon");
            if (depEl is not null && string.Equals(depEl.Value.Trim(), depTrim, StringComparison.Ordinal))
                return $"# No change: DependentUpon already set ({updateNorm}).";

            if (dryRun)
                return $"# Would set DependentUpon on Compile Update=\"{updateNorm}\" → \"{depTrim}\".";

            if (depEl is not null)
                depEl.Value = depTrim;
            else
                existing.AddFirst(new XElement(rootNs + "DependentUpon", depTrim));
            changed = true;
            return $"Updated DependentUpon for {updateNorm} → {depTrim}";
        }

        if (dryRun)
            return $"# Would add Compile Update=\"{updateNorm}\" with DependentUpon=\"{depTrim}\".";

        doc.Root!.Add(
            new XElement(rootNs + "ItemGroup",
                new XElement(rootNs + "Compile",
                    new XAttribute("Update", updateNorm),
                    new XElement(rootNs + "DependentUpon", depTrim))));
        changed = true;
        return $"Added Compile Update for {updateNorm} → DependentUpon {depTrim}";
    }
}
