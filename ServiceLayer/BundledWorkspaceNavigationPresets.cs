#nullable enable

namespace RoslynMcp.ServiceLayer.WorkspaceNavigation;

/// <summary>
/// Те же id и семантика, что у встроенных пресетов Cascade IDE (<c>WorkspaceNavigation/presets.toml</c>), в виде JSON для <see cref="WorkspaceNavigationPresetMerge"/>.
/// В roslyn-mcp нет чтения файлов настроек — только этот встроенный набор.
/// </summary>
internal static class BundledWorkspaceNavigationPresets
{
    /// <summary>JSON-объект: preset id → { include_kinds?, exclude_kinds? }.</summary>
    internal const string Json = """
{
  "peers_only": { "include_kinds": ["partial_peer", "project_peer"] },
  "no_namespace_noise": { "exclude_kinds": ["same_namespace", "same_directory"] },
  "tests_and_peers": { "include_kinds": ["partial_peer", "project_peer", "test_counterpart"] },
  "structure_only": { "include_kinds": ["partial_peer", "project_peer", "xaml_codebehind_pair", "same_directory"] }
}
""";
}
