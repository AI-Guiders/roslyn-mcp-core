using System.Collections.Immutable;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Глобальные свойства MSBuild для всех вызовов <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> в RoslynMcp.
/// <list type="bullet">
/// <item><description><c>RoslynMcpWorkspace=true</c> — потребители (например CascadeIDE) могут не подключать project-reference на локальные анализаторы,
/// чтобы сборка анализатора не конфликтовала с удержанием того же DLL в памяти процесса RoslynMcp.</description></item>
/// </list>
/// </summary>
public static class RoslynMcpWorkspaceProperties
{
    /// <summary>Совпадает с прежней логикой <c>GetDiagnostics</c>: полноценная компиляция и source generators, не design-only.</summary>
    public static ImmutableDictionary<string, string> MsBuild { get; } =
        ImmutableDictionary.CreateRange<string, string>([
            new("DesignTimeBuild", "false"),
            new("SkipCompilerExecution", "false"),
            new("RoslynMcpWorkspace", "true"),
        ]);
}
