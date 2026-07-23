using System.Diagnostics;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Explicit Code Cleanup intent via <c>dotnet format</c> CLI (editorconfig + style/analyzers).
/// Not part of Extract / soft Format — call only when asked.
/// Returns <see cref="ToolStepJson"/> (<c>kind=roslyn.cleanup</c>).
/// </summary>
public static class CleanupDocument
{
    public const string Kind = "roslyn.cleanup";

    /// <param name="apply">false — <c>--verify-no-changes</c> (dry run).</param>
    /// <param name="profile">
    /// null/empty — full <c>dotnet format</c>;
    /// <c>whitespace</c> | <c>style</c> | <c>analyzers</c> — subcommand.
    /// </param>
    /// <param name="filePath">Optional single file (<c>--include</c> relative to project/solution dir).</param>
    public static async Task<string> CleanupAsync(
        string solutionOrProjectPath,
        string? filePath = null,
        bool apply = true,
        string? profile = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionOrProjectPath))
            return ToolStepJson.Fail(Kind, $"solution/project not found: {solutionOrProjectPath}");

        var workspace = Path.GetFullPath(solutionOrProjectPath.Trim());
        var workDir = Path.GetDirectoryName(workspace);
        if (string.IsNullOrEmpty(workDir))
            return ToolStepJson.Fail(Kind, "cannot resolve working directory");

        string? includeRel = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                return ToolStepJson.Fail(Kind, $"file not found: {filePath}");
            var fullFile = Path.GetFullPath(filePath.Trim());
            includeRel = Path.GetRelativePath(workDir, fullFile);
            if (includeRel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || includeRel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || includeRel == "..")
                return ToolStepJson.Fail(Kind, $"file is outside workspace directory: {filePath}");
        }

        var profileNorm = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim().ToLowerInvariant();
        if (profileNorm is not null and not ("whitespace" or "style" or "analyzers"))
            return ToolStepJson.Fail(Kind, "profile must be whitespace | style | analyzers (or omit for full cleanup)");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("format");
        if (profileNorm is not null)
            psi.ArgumentList.Add(profileNorm);
        psi.ArgumentList.Add(workspace);
        if (!apply)
            psi.ArgumentList.Add("--verify-no-changes");
        if (includeRel is not null)
        {
            psi.ArgumentList.Add("--include");
            psi.ArgumentList.Add(includeRel);
        }
        psi.ArgumentList.Add("--verbosity");
        psi.ArgumentList.Add("minimal");

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return ToolStepJson.Fail(Kind, "failed to start dotnet format");
        }
        catch (Exception ex)
        {
            return ToolStepJson.Fail(Kind, $"failed to start dotnet format: {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = (await stdoutTask.ConfigureAwait(false)).TrimEnd();
        var stderr = (await stderrTask.ConfigureAwait(false)).TrimEnd();

        var scope = profileNorm ?? "full";
        var target = includeRel ?? "(project/solution)";
        var wouldChange = !apply && proc.ExitCode != 0;
        var changedOrApplied = apply && proc.ExitCode == 0;
        var failed = apply && proc.ExitCode != 0;

        var data = new
        {
            apply,
            profile = scope,
            workspace,
            target,
            exit_code = proc.ExitCode,
            would_change = wouldChange ? true : (bool?)null,
            changed = apply ? changedOrApplied : (bool?)null,
            stdout = string.IsNullOrEmpty(stdout) ? null : stdout,
            stderr = string.IsNullOrEmpty(stderr) ? null : stderr
        };

        if (failed)
            return ToolStepJson.Fail(Kind, "cleanup failed", data);

        var summary = !apply
            ? (proc.ExitCode == 0 ? "No cleanup changes." : "Would change (dry run).")
            : "Cleanup finished.";

        // Dry-run "would change" is still ok=true (intent succeeded; no write requested).
        return ToolStepJson.Ok(Kind, summary, data);
    }
}
