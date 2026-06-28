namespace RoslynMcp.ServiceLayer;

/// <summary>Чтение стиля кода из .editorconfig (от каталога файла вверх по дереву; ближайший к файлу переопределяет).</summary>
public static class EditorConfigStyle
{
    private const string PreferIntrinsicLocalsKey = "dotnet_style_predefined_type_for_locals_parameters_members";
    private const string PreferIntrinsicMemberAccessKey = "dotnet_style_predefined_type_for_member_access";
    private const string VarForBuiltInKey = "csharp_style_var_for_built_in_types";
    private const string VarWhenApparentKey = "csharp_style_var_when_type_is_apparent";
    private const string VarElsewhereKey = "csharp_style_var_elsewhere";
    private const string PreferBracesKey = "csharp_prefer_braces";
    private const string NewLineBeforeOpenBraceKey = "csharp_new_line_before_open_brace";
    private const string IndentStyleKey = "indent_style";
    private const string IndentSizeKey = "indent_size";
    private const string TabWidthKey = "tab_width";

    /// <summary>Собирает .editorconfig от directory вверх до корня, парсит [*.cs] и объединяет (ближайший к directory выигрывает).</summary>
    public static EditorStyleOptions GetOptionsForDirectory(string directory)
    {
        var dir = Path.GetFullPath(directory.Trim());
        if (!Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? dir;

        var configPaths = new List<string>();
        while (!string.IsNullOrEmpty(dir))
        {
            var editorConfigPath = Path.Combine(dir, ".editorconfig");
            if (File.Exists(editorConfigPath))
                configPaths.Add(editorConfigPath);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent ?? "";
        }

        if (configPaths.Count == 0)
            return EditorStyleOptions.Default;

        // configPaths[0] = ближайший к directory, configPaths[last] = корень. Применяем от корня к directory, чтобы ближайший переопределял.
        EditorStyleOptions? merged = null;
        for (var i = configPaths.Count - 1; i >= 0; i--)
        {
            var parsed = ParseEditorConfig(configPaths[i]);
            if (parsed != null)
                merged = parsed;
        }

        return merged ?? EditorStyleOptions.Default;
    }

    /// <summary>Собирает из .editorconfig (от directory вверх) множество ID диагностик с severity = none (их не показывать в выдаче roslyn_get_diagnostics).</summary>
    public static HashSet<string> GetDiagnosticIdsSeverityNone(string directory)
    {
        var dir = Path.GetFullPath(directory.Trim());
        if (!Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? dir;

        var configPaths = new List<string>();
        while (!string.IsNullOrEmpty(dir))
        {
            var editorConfigPath = Path.Combine(dir, ".editorconfig");
            if (File.Exists(editorConfigPath))
                configPaths.Add(editorConfigPath);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent ?? "";
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = configPaths.Count - 1; i >= 0; i--)
        {
            try
            {
                var lines = File.ReadAllLines(configPaths[i]);
                var inRelevantSection = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('['))
                    {
                        inRelevantSection = trimmed.Contains('*') && (trimmed.Contains("*.cs", StringComparison.OrdinalIgnoreCase) || trimmed == "[*]");
                        continue;
                    }
                    if (!inRelevantSection)
                        continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = trimmed[..eq].Trim();
                    if (!key.StartsWith("dotnet_diagnostic.", StringComparison.Ordinal) || !key.EndsWith(".severity", StringComparison.Ordinal))
                        continue;
                    var id = key["dotnet_diagnostic.".Length..^".severity".Length].Trim();
                    var value = trimmed[(eq + 1)..].Trim();
                    var valuePart = value.Contains(':') ? value[..value.IndexOf(':')].Trim() : value.Trim();
                    if (valuePart.Equals("none", StringComparison.OrdinalIgnoreCase))
                        result.Add(id);
                    else
                        result.Remove(id);
                }
            }
            catch
            {
                // ignore
            }
        }

        return result;
    }

    /// <summary>Парсит один .editorconfig: секция [*.cs], из значений берётся часть до двоеточия (severity отбрасывается).</summary>
    private static EditorStyleOptions? ParseEditorConfig(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var inCsSection = false;
            bool? preferIntrinsicLocals = null;
            bool? preferIntrinsicMemberAccess = null;
            bool? varForBuiltIn = null;
            bool? varWhenApparent = null;
            bool? varElsewhere = null;
            bool? preferBraces = null;
            string? newLineBeforeOpenBrace = null;
            string? indentStyle = null;
            int? indentSize = null;
            int? tabWidth = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    inCsSection = trimmed.Contains("*.cs", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inCsSection)
                    continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = trimmed[..eq].Trim();
                var value = trimmed[(eq + 1)..].Trim();
                var valuePart = value.Contains(':') ? value[..value.IndexOf(':')].Trim() : value.Trim();
                var isTrue = valuePart.Equals("true", StringComparison.OrdinalIgnoreCase);

                if (key.Equals(PreferIntrinsicLocalsKey, StringComparison.OrdinalIgnoreCase))
                    preferIntrinsicLocals = isTrue;
                else if (key.Equals(PreferIntrinsicMemberAccessKey, StringComparison.OrdinalIgnoreCase))
                    preferIntrinsicMemberAccess = isTrue;
                else if (key.Equals(VarForBuiltInKey, StringComparison.OrdinalIgnoreCase))
                    varForBuiltIn = isTrue;
                else if (key.Equals(VarWhenApparentKey, StringComparison.OrdinalIgnoreCase))
                    varWhenApparent = isTrue;
                else if (key.Equals(VarElsewhereKey, StringComparison.OrdinalIgnoreCase))
                    varElsewhere = isTrue;
                else if (key.Equals(PreferBracesKey, StringComparison.OrdinalIgnoreCase))
                    preferBraces = isTrue;
                else if (key.Equals(NewLineBeforeOpenBraceKey, StringComparison.OrdinalIgnoreCase))
                    newLineBeforeOpenBrace = valuePart;
                else if (key.Equals(IndentStyleKey, StringComparison.OrdinalIgnoreCase))
                    indentStyle = valuePart;
                else if (key.Equals(IndentSizeKey, StringComparison.OrdinalIgnoreCase) && int.TryParse(valuePart, out var size))
                    indentSize = size;
                else if (key.Equals(TabWidthKey, StringComparison.OrdinalIgnoreCase) && int.TryParse(valuePart, out var tw))
                    tabWidth = tw;
            }

            return new EditorStyleOptions(
                preferIntrinsicTypeNames: preferIntrinsicLocals ?? preferIntrinsicMemberAccess ?? true,
                preferVarForBuiltInTypes: varForBuiltIn ?? false,
                preferVarWhenTypeApparent: varWhenApparent ?? false,
                preferVarElsewhere: varElsewhere ?? false,
                preferBraces: preferBraces ?? true,
                newLineBeforeOpenBrace: newLineBeforeOpenBrace ?? "all",
                indentStyle: indentStyle ?? "space",
                indentSize: indentSize ?? 4,
                tabWidth: tabWidth ?? 4);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Опции стиля для генерации кода (по .editorconfig или по умолчанию).</summary>
public sealed class EditorStyleOptions
{
    public static EditorStyleOptions Default { get; } = new();

    public bool PreferIntrinsicTypeNames { get; }
    public bool PreferVarForBuiltInTypes { get; }
    public bool PreferVarWhenTypeApparent { get; }
    public bool PreferVarElsewhere { get; }
    public bool PreferBraces { get; }
    public string NewLineBeforeOpenBrace { get; }
    public string IndentStyle { get; }
    public int IndentSize { get; }
    public int TabWidth { get; }

    private readonly string _newLine = Environment.NewLine;

    /// <summary>Строка отступа для одного уровня (пробелы или таб по indent_style/indent_size/tab_width).</summary>
    public string IndentString =>
        IndentStyle.Equals("tab", StringComparison.OrdinalIgnoreCase)
            ? new string('\t', 1)
            : new string(' ', IndentSize > 0 ? IndentSize : 4);

    public EditorStyleOptions(
        bool preferIntrinsicTypeNames = true,
        bool preferVarForBuiltInTypes = false,
        bool preferVarWhenTypeApparent = false,
        bool preferVarElsewhere = false,
        bool preferBraces = true,
        string? newLineBeforeOpenBrace = "all",
        string? indentStyle = "space",
        int indentSize = 4,
        int tabWidth = 4)
    {
        PreferIntrinsicTypeNames = preferIntrinsicTypeNames;
        PreferVarForBuiltInTypes = preferVarForBuiltInTypes;
        PreferVarWhenTypeApparent = preferVarWhenTypeApparent;
        PreferVarElsewhere = preferVarElsewhere;
        PreferBraces = preferBraces;
        NewLineBeforeOpenBrace = newLineBeforeOpenBrace ?? "all";
        IndentStyle = indentStyle ?? "space";
        IndentSize = indentSize;
        TabWidth = tabWidth;
    }

    /// <summary>Заменяет отображаемые имена типов .NET на ключевые слова C# (Int32 → int и т.д.), если PreferIntrinsicTypeNames = true.</summary>
    public string FormatTypeName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName) || !PreferIntrinsicTypeNames)
            return displayName;

        var s = displayName.AsSpan().Trim();
        var nullable = false;
        if (s.EndsWith("?", StringComparison.Ordinal))
        {
            nullable = true;
            s = s[..^1].Trim();
        }

        var typePart = s.ToString();
        if (typePart.StartsWith("System.", StringComparison.Ordinal))
            typePart = typePart["System.".Length..];
        var replacement = typePart switch
        {
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "UInt32" => "uint",
            "UInt64" => "ulong",
            "UInt16" => "ushort",
            "Byte" => "byte",
            "SByte" => "sbyte",
            "Single" => "float",
            "Double" => "double",
            "Decimal" => "decimal",
            "Boolean" => "bool",
            "String" => "string",
            "Object" => "object",
            "Void" => "void",
            _ => null
        };

        if (replacement != null)
            return nullable ? replacement + "?" : replacement;
        return nullable ? typePart + "?" : typePart;
    }

    /// <summary>Новая строка перед открывающей скобкой по опции csharp_new_line_before_open_brace (all/none и т.д.).</summary>
    public string NewLine => _newLine;

    /// <summary>Для однострочного блока: если PreferBraces = true, возвращает " { }"; иначе ";" (для выражений мы всё равно генерируем с телом).</summary>
    public string OpenBraceOrSpace => PreferBraces ? " {" : "";
    public string CloseBraceOrEmpty => PreferBraces ? " }" : "";
}
