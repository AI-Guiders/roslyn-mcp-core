using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Диагностики: scope=syntax|file (ParseText) / project|solution (MSBuild via <see cref="MsBuildWorkspaceHost"/>).
/// Results cached by path+fingerprint until invalidate (file stable ⇒ diag stable).
/// Default: file_path set → syntax; иначе project.
/// </summary>
public static class GetDiagnostics
{
    public const string Kind = "roslyn.get_diagnostics";

    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        if (p.EndsWith(Path.DirectorySeparatorChar))
            p = p.TrimEnd(Path.DirectorySeparatorChar);
        return p;
    }

    /// <summary>Исключаем артефакты сборки (obj/, bin/), чтобы не засорять вывод.</summary>
    private static bool IsBuildArtifactPath(string normalizedPath)
    {
        return normalizedPath.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalizedPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalizedPath.Contains("/obj/", StringComparison.Ordinal)
            || normalizedPath.Contains("/bin/", StringComparison.Ordinal);
    }

    /// <summary>
    /// scope null → file_path? syntax : project.
    /// Explicit: syntax|parse|file / project|msbuild|full|semantic.
    /// </summary>
    public static string ResolveScope(string? scope, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope.Trim().ToLowerInvariant() switch
            {
                "syntax" or "parse" or "file" => "syntax",
                "project" or "msbuild" or "full" or "semantic" => "project",
                "solution" or "sln" => "solution",
                _ => throw new ArgumentException(
                    "scope must be syntax|project|solution (aliases: parse|file / msbuild|full|semantic / sln).")
            };
        }

        return string.IsNullOrWhiteSpace(filePath) ? "project" : "syntax";
    }

    /// <summary>
    /// Запускает source generators и возвращает <see cref="Compilation"/>.
    /// После <see cref="Project.GetSourceGeneratedDocumentsAsync"/> обновляется <see cref="Workspace.CurrentSolution"/>;
    /// старый снимок <see cref="Project"/> остаётся без сгенерированных синтаксических деревьев — повторно берём проект из workspace.
    /// </summary>
    private static async Task<Compilation?> GetCompilationAfterSourceGeneratorsAsync(
        Workspace workspace,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var project = workspace.CurrentSolution.GetProject(projectId);
        if (project is null)
            return null;
        await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
        project = workspace.CurrentSolution.GetProject(projectId);
        if (project is null)
            return null;
        return await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (DiagnosticSeverity effective, bool isSuppressed) GetEffectiveSeverity(Diagnostic d, CompilationOptions options)
    {
        var report = options.SpecificDiagnosticOptions.GetValueOrDefault(d.Id, ReportDiagnostic.Default);
        if (report == ReportDiagnostic.Suppress)
            return (default, true);

        var severity = report switch
        {
            ReportDiagnostic.Error => DiagnosticSeverity.Error,
            ReportDiagnostic.Warn => DiagnosticSeverity.Warning,
            ReportDiagnostic.Info => DiagnosticSeverity.Info,
            ReportDiagnostic.Hidden => DiagnosticSeverity.Hidden,
            _ => d.Severity // Default → оставляем дефолтную серьёзность диагностики
        };

        // Treat warnings as errors (GeneralDiagnosticOption)
        if (options.GeneralDiagnosticOption == ReportDiagnostic.Error && severity == DiagnosticSeverity.Warning)
            severity = DiagnosticSeverity.Error;

        return (severity, false);
    }

    /// <summary>Парсит .slnx: возвращает полные пути к .csproj (относительные пути разрешаются от каталога slnx).</summary>
    private static List<string> GetProjectPathsFromSlnx(string slnxPath)
    {
        var dir = Path.GetDirectoryName(slnxPath) ?? "";
        var xml = XDocument.Load(slnxPath);
        var projects = xml.Root?
            .Elements("Project")
            .Select(e => (string?)e.Attribute("Path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p!.Trim())))
            .Where(File.Exists)
            .ToList() ?? [];
        return projects;
    }

    private static async Task<(int totalAfterPath, int excludedSeverityNone, int excludedSuppress)> CollectDiagnosticsFromSolution(
        Workspace workspace,
        Solution solution,
        string? targetPath,
        List<(Diagnostic d, DiagnosticSeverity effectiveSeverity)> allDiagnostics,
        CancellationToken cancellationToken)
    {
        var totalAfterPath = 0;
        var excludedSeverityNone = 0;
        var excludedSuppress = 0;
        foreach (var projectId in solution.ProjectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectForPaths = workspace.CurrentSolution.GetProject(projectId);
            if (projectForPaths is null)
                continue;
            var projectDir = Path.GetDirectoryName(projectForPaths.FilePath) ?? "";
            var severityNoneIds = EditorConfigStyle.GetDiagnosticIdsSeverityNone(projectDir);

            var comp = await GetCompilationAfterSourceGeneratorsAsync(workspace, projectId, cancellationToken).ConfigureAwait(false);
            if (comp is null)
                continue;
            var compOptions = comp.Options;

            foreach (var d in comp.GetDiagnostics(cancellationToken))
            {
                if (d.Location.SourceTree?.FilePath is null) continue;
                var treePath = NormalizePath(d.Location.SourceTree.FilePath);
                if (IsBuildArtifactPath(treePath)) continue;
                if (targetPath is not null && !string.Equals(treePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                totalAfterPath++;
                if (severityNoneIds.Contains(d.Id)) { excludedSeverityNone++; continue; }
                var (effective, isSuppressed) = GetEffectiveSeverity(d, compOptions);
                if (isSuppressed) { excludedSuppress++; continue; }
                allDiagnostics.Add((d, effective));
            }

            var project = workspace.CurrentSolution.GetProject(projectId);
            if (project is null)
                continue;
            var analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(project.Language))
                .ToImmutableArray();
            if (!analyzers.IsEmpty)
            {
                var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);
                var options = new CompilationWithAnalyzersOptions(
                    analyzerOptions,
                    onAnalyzerException: (_, _, _) => { },
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: false);
                var cwa = new CompilationWithAnalyzers(comp, analyzers, options);
                var result = await cwa.GetAnalysisResultAsync(cancellationToken).ConfigureAwait(false);
                foreach (var d in result.GetAllDiagnostics())
                {
                    if (d.Location.SourceTree?.FilePath is null) continue;
                    var treePath = NormalizePath(d.Location.SourceTree.FilePath);
                    if (IsBuildArtifactPath(treePath)) continue;
                    if (targetPath is not null && !string.Equals(treePath, targetPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    totalAfterPath++;
                    if (severityNoneIds.Contains(d.Id)) { excludedSeverityNone++; continue; }
                    var (effective, isSuppressed) = GetEffectiveSeverity(d, compOptions);
                    if (isSuppressed) { excludedSuppress++; continue; }
                    allDiagnostics.Add((d, effective));
                }
            }
        }
        return (totalAfterPath, excludedSeverityNone, excludedSuppress);
    }

    public static Task<string> GetDiagnosticsAsync(
        string solutionOrProjectPath,
        string? filePath,
        CancellationToken cancellationToken = default) =>
        GetDiagnosticsAsync(solutionOrProjectPath, filePath, scope: null, sourceText: null, cancellationToken);

    /// <param name="scope">null → file_path? syntax : project. syntax = ParseText only; project = MSBuild (serialized).</param>
    /// <param name="sourceText">Optional buffer text for scope=syntax.</param>
    public static async Task<string> GetDiagnosticsAsync(
        string solutionOrProjectPath,
        string? filePath,
        string? scope,
        string? sourceText,
        CancellationToken cancellationToken = default)
    {
        var resolvedScope = ResolveScope(scope, filePath);
        if (resolvedScope == "syntax")
            return GetSyntaxDiagnostics(solutionOrProjectPath, filePath, sourceText, cancellationToken);

        if (!File.Exists(solutionOrProjectPath))
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");

        // project + solution: same MSBuild path; solution prefers .sln key when caller passes it.
        return await GetProjectDiagnosticsCachedAsync(
                solutionOrProjectPath, filePath, resolvedScope, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetSyntaxDiagnostics(
        string solutionOrProjectPath,
        string? filePath,
        string? sourceText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return ToolStepJson.Fail(Kind, "scope=syntax requires file_path");

        if (!File.Exists(filePath) && string.IsNullOrEmpty(sourceText))
            return ToolStepJson.Fail(Kind, $"file not found: {filePath}");

        var targetPath = NormalizePath(filePath);
        var text = sourceText ?? File.ReadAllText(filePath);
        var fingerprint = DiagnosticsResultCache.FingerprintText(text);
        if (DiagnosticsResultCache.TryGet("syntax", targetPath, fingerprint, out var cached))
            return MarkCacheHit(cached);

        var tree = CSharpSyntaxTree.ParseText(
            text,
            path: targetPath,
            cancellationToken: cancellationToken);

        var allDiagnostics = new List<(Diagnostic d, DiagnosticSeverity effectiveSeverity)>();
        foreach (var d in tree.GetDiagnostics(cancellationToken))
        {
            if (d.Severity == DiagnosticSeverity.Hidden)
                continue;
            allDiagnostics.Add((d, d.Severity));
        }

        var payload = FormatDiagnosticsPayload(
            solutionOrProjectPath,
            targetPath,
            scope: "syntax",
            allDiagnostics,
            totalAfterPath: allDiagnostics.Count,
            excludedSeverityNone: 0,
            excludedSuppress: 0,
            cache: "miss");
        DiagnosticsResultCache.Set("syntax", targetPath, fingerprint, payload);
        return payload;
    }

    private static async Task<string> GetProjectDiagnosticsCachedAsync(
        string solutionOrProjectPath,
        string? filePath,
        string resolvedScope,
        CancellationToken cancellationToken)
    {
        string? targetPath = null;
        var fingerprintParts = new List<string> { DiagnosticsResultCache.FingerprintFile(solutionOrProjectPath) };
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                return ToolStepJson.Fail(Kind, $"file not found: {filePath}");
            targetPath = NormalizePath(filePath);
            fingerprintParts.Add(DiagnosticsResultCache.FingerprintFile(filePath));
        }

        var pathKey = targetPath ?? NormalizePath(solutionOrProjectPath);
        var fingerprint = string.Join('|', fingerprintParts);
        if (DiagnosticsResultCache.TryGet(resolvedScope, pathKey, fingerprint, out var cached))
            return MarkCacheHit(cached);

        var payload = await GetProjectDiagnosticsComputeAsync(
                solutionOrProjectPath, targetPath, resolvedScope, cancellationToken)
            .ConfigureAwait(false);
        if (!payload.Contains("\"ok\":false", StringComparison.Ordinal)
            && !payload.Contains("\"ok\": false", StringComparison.Ordinal))
            DiagnosticsResultCache.Set(resolvedScope, pathKey, fingerprint, payload);
        return payload;
    }

    private static async Task<string> GetProjectDiagnosticsComputeAsync(
        string solutionOrProjectPath,
        string? targetPath,
        string resolvedScope,
        CancellationToken cancellationToken)
    {
        var allDiagnostics = new List<(Diagnostic d, DiagnosticSeverity effectiveSeverity)>();
        var totalAfterPath = 0;
        var excludedSeverityNone = 0;
        var excludedSuppress = 0;

        var ext = Path.GetExtension(solutionOrProjectPath);
        if (string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var projectPaths = GetProjectPathsFromSlnx(solutionOrProjectPath);
            if (projectPaths.Count == 0)
                return ToolStepJson.Fail(Kind, ".slnx contains no valid project paths or files not found");

            // slnx: still per-project open (no single MSBuild solution object).
            await MsBuildWorkspaceGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var projectPath in projectPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
                    try
                    {
                        var solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, projectPath, cancellationToken).ConfigureAwait(false);
                        if (solution is not null)
                        {
                            var (t, n, s) = await CollectDiagnosticsFromSolution(workspace, solution, targetPath, allDiagnostics, cancellationToken).ConfigureAwait(false);
                            totalAfterPath += t;
                            excludedSeverityNone += n;
                            excludedSuppress += s;
                        }
                    }
                    finally
                    {
                        workspace.Dispose();
                    }
                }
            }
            finally
            {
                MsBuildWorkspaceGate.Gate.Release();
            }
        }
        else
        {
            try
            {
                var (t, n, s) = await MsBuildWorkspaceHost.RunAsync(
                    solutionOrProjectPath,
                    async (workspace, solution, ct) =>
                        await CollectDiagnosticsFromSolution(workspace, solution, targetPath, allDiagnostics, ct)
                            .ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                totalAfterPath += t;
                excludedSeverityNone += n;
                excludedSuppress += s;
            }
            catch (InvalidOperationException ex)
            {
                return ToolStepJson.Fail(Kind, ex.Message);
            }
        }

        return FormatDiagnosticsPayload(
            solutionOrProjectPath,
            targetPath,
            scope: resolvedScope,
            allDiagnostics,
            totalAfterPath,
            excludedSeverityNone,
            excludedSuppress,
            cache: "miss");
    }

    static string MarkCacheHit(string payloadJson) =>
        payloadJson
            .Replace("\"cache\":\"miss\"", "\"cache\":\"hit\"", StringComparison.Ordinal)
            .Replace("cache=miss", "cache=hit", StringComparison.Ordinal);

    private static string FormatDiagnosticsPayload(
        string workspacePath,
        string? targetPath,
        string scope,
        List<(Diagnostic d, DiagnosticSeverity effectiveSeverity)> allDiagnostics,
        int totalAfterPath,
        int excludedSeverityNone,
        int excludedSuppress,
        string cache = "miss")
    {
        var items = new List<object>();
        foreach (var (d, effectiveSeverity) in allDiagnostics.OrderBy(x => x.d.Location.SourceTree?.FilePath ?? "").ThenBy(x => x.d.Location.SourceSpan.Start))
        {
            var tree = d.Location.SourceTree;
            var file = tree?.FilePath ?? "(no file)";
            var line = 0;
            var column = 0;
            var endLine = 0;
            var endColumn = 0;
            if (tree is not null && d.Location.IsInSource)
            {
                var lineSpan = d.Location.GetLineSpan();
                line = lineSpan.StartLinePosition.Line + 1;
                column = lineSpan.StartLinePosition.Character + 1;
                endLine = lineSpan.EndLinePosition.Line + 1;
                endColumn = lineSpan.EndLinePosition.Character + 1;
            }
            var severityStr = effectiveSeverity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                DiagnosticSeverity.Info => "info",
                DiagnosticSeverity.Hidden => "hidden",
                _ => "unknown"
            };
            items.Add(new
            {
                file,
                line,
                column,
                end_line = endLine,
                end_column = endColumn,
                severity = severityStr,
                id = d.Id,
                message = d.GetMessage(CultureInfo.InvariantCulture)
            });
        }

        var errorCount = allDiagnostics.Count(x => x.effectiveSeverity == DiagnosticSeverity.Error);

        return ToolStepJson.Ok(Kind, $"scope={scope} shown={allDiagnostics.Count} errors={errorCount} cache={cache}", new
        {
            scope,
            cache,
            workspace = workspacePath,
            file = targetPath,
            total_after_path = totalAfterPath,
            excluded_severity_none = excludedSeverityNone,
            excluded_suppress = excludedSuppress,
            shown = allDiagnostics.Count,
            error_count = errorCount,
            items
        });
    }
}
