#nullable enable

namespace RoslynMcp.ServiceLayer.WorkspaceNavigation;

/// <summary>Пути для навигации: тот же MSBuild-эвристики, что в Cascade IDE <c>McpSolutionTree</c> (без общего кода).</summary>
public static class WorkspaceNavigationPathHelpers
{
    /// <summary>
    /// Ближайший вверх по диску <c>.csproj</c>, которому по соглашению принадлежит файл.
    /// </summary>
    public static string? ResolveOwningProjectPath(string fileFullPath)
    {
        if (string.IsNullOrWhiteSpace(fileFullPath))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(fileFullPath.Trim());
        }
        catch
        {
            return null;
        }

        var dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir))
        {
            string[] csprojs;
            try
            {
                csprojs = Directory.GetFiles(dir, "*.csproj");
            }
            catch
            {
                break;
            }

            if (csprojs.Length > 0)
            {
                if (csprojs.Length == 1)
                    return Path.GetFullPath(csprojs[0]);
                var folderName = Path.GetFileName(dir);
                var match = csprojs.FirstOrDefault(p =>
                    string.Equals(Path.GetFileNameWithoutExtension(p), folderName, StringComparison.OrdinalIgnoreCase));
                return Path.GetFullPath(match ?? csprojs[0]);
            }

            try
            {
                dir = Path.GetDirectoryName(dir);
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    public static bool IsBuildArtifactPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;
        return fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetRelativePath(string? solutionPath, string? fullPath)
    {
        if (string.IsNullOrEmpty(solutionPath) || string.IsNullOrEmpty(fullPath))
            return null;
        var solutionDir = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrEmpty(solutionDir))
            return null;
        try
        {
            return Path.GetRelativePath(solutionDir, fullPath);
        }
        catch
        {
            return null;
        }
    }
}
