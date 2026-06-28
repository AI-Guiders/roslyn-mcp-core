#nullable enable

namespace RoslynMcp.ServiceLayer.WorkspaceNavigation;

/// <summary>Имена видов связей в ответе (режим related / основа subgraph).</summary>
public static class WorkspaceNavigationRelatedKinds
{
    public const string PartialPeer = "partial_peer";
    public const string ProjectPeer = "project_peer";
    public const string XamlCodeBehindPair = "xaml_codebehind_pair";
    public const string TestCounterpart = "test_counterpart";
    public const string SameNamespace = "same_namespace";
    public const string SameDirectory = "same_directory";

    internal static readonly string[] All =
    [
        PartialPeer,
        ProjectPeer,
        XamlCodeBehindPair,
        TestCounterpart,
        SameNamespace,
        SameDirectory
    ];

    /// <summary>Каноническое имя вида или <c>null</c>, если токен не известен.</summary>
    public static string? TryCanonicalKind(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var s = token.Trim();
        foreach (var k in All)
        {
            if (string.Equals(k, s, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        return null;
    }
}
