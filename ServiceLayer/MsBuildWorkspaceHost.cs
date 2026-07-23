using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Process-scoped MSBuild workspace cache (HCI-like): one open solution/project at a time.
/// All access serialized via <see cref="MsBuildWorkspaceGate"/>. CDP warms on cdp_open;
/// tools reuse instead of Create+Open+Dispose per call.
/// </summary>
public static class MsBuildWorkspaceHost
{
    static readonly object StateLock = new();
    static string? _openKey;
    static MSBuildWorkspace? _workspace;

    static string NormalizeKey(string path) =>
        Path.GetFullPath(path.Trim());

    /// <summary>Background-friendly warm after project open. No-op if path missing.</summary>
    public static async Task WarmAsync(string solutionOrProjectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionOrProjectPath) || !File.Exists(solutionOrProjectPath))
            return;

        await MsBuildWorkspaceGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenUnlockedAsync(solutionOrProjectPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MsBuildWorkspaceGate.Gate.Release();
        }
    }

    /// <summary>
    /// Run work against the cached solution. Holds the MSBuild gate for the whole call.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        string solutionOrProjectPath,
        Func<MSBuildWorkspace, Solution, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        await MsBuildWorkspaceGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (ws, solution) = await EnsureOpenUnlockedAsync(solutionOrProjectPath, cancellationToken)
                .ConfigureAwait(false);
            return await body(ws, solution, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MsBuildWorkspaceGate.Gate.Release();
        }
    }

    /// <summary>Drop cache (project close / path change / failed open).</summary>
    public static void Invalidate()
    {
        lock (StateLock)
            DisposeUnlocked();
    }

    /// <summary>
    /// Push buffer text into the open solution (after cdp_buffer flush). Returns false if no matching doc.
    /// </summary>
    public static bool TryApplyDocumentText(string filePath, string text)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var full = NormalizeKey(filePath);
        MsBuildWorkspaceGate.Gate.Wait();
        try
        {
            lock (StateLock)
            {
                if (_workspace is null)
                    return false;

                var doc = _workspace.CurrentSolution.Projects
                    .SelectMany(p => p.Documents)
                    .FirstOrDefault(d =>
                        d.FilePath is { } fp
                        && string.Equals(NormalizeKey(fp), full, StringComparison.OrdinalIgnoreCase));
                if (doc is null)
                    return false;

                var updated = _workspace.CurrentSolution.WithDocumentText(doc.Id, SourceText.From(text));
                return _workspace.TryApplyChanges(updated);
            }
        }
        finally
        {
            MsBuildWorkspaceGate.Gate.Release();
        }
    }

    /// <summary>Caller must hold <see cref="MsBuildWorkspaceGate"/>.</summary>
    internal static async Task<(MSBuildWorkspace Workspace, Solution Solution)> EnsureOpenUnlockedAsync(
        string solutionOrProjectPath,
        CancellationToken cancellationToken)
    {
        var key = NormalizeKey(solutionOrProjectPath);
        lock (StateLock)
        {
            if (_workspace is not null
                && string.Equals(_openKey, key, StringComparison.OrdinalIgnoreCase))
                return (_workspace, _workspace.CurrentSolution);
        }

        // Open outside StateLock (async); still under gate so no parallel open.
        lock (StateLock)
            DisposeUnlocked();

        MsBuildLocatorOnce.EnsureRegistered();
        var workspace = MSBuildWorkspace.Create(RoslynMcpWorkspaceProperties.MsBuild);
        Solution? solution;
        try
        {
            solution = await WorkspaceOpen.OpenSolutionOrProjectAsync(workspace, key, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        if (solution is null)
        {
            workspace.Dispose();
            throw new InvalidOperationException($"failed to open solution/project: {key}");
        }

        lock (StateLock)
        {
            _workspace = workspace;
            _openKey = key;
            return (workspace, workspace.CurrentSolution);
        }
    }

    static void DisposeUnlocked()
    {
        _workspace?.Dispose();
        _workspace = null;
        _openKey = null;
    }
}
