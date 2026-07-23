using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Diagnostics are stable while source fingerprint + scope stay the same.
/// Invalidate on mutate / project close — do not recompute on every tool call.
/// </summary>
public static class DiagnosticsResultCache
{
    static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.Ordinal);

    sealed record Entry(string PayloadJson, DateTime Utc);

    static string MakeKey(string scope, string pathKey, string fingerprint) =>
        $"{scope}\u001f{pathKey}\u001f{fingerprint}";

    public static string FingerprintText(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public static string FingerprintFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists)
            return "missing";
        return $"{fi.Length:x}:{fi.LastWriteTimeUtc.Ticks:x}";
    }

    public static bool TryGet(string scope, string pathKey, string fingerprint, out string payloadJson)
    {
        var key = MakeKey(scope, Normalize(pathKey), fingerprint);
        if (Cache.TryGetValue(key, out var entry))
        {
            payloadJson = entry.PayloadJson;
            return true;
        }

        payloadJson = "";
        return false;
    }

    public static void Set(string scope, string pathKey, string fingerprint, string payloadJson)
    {
        var key = MakeKey(scope, Normalize(pathKey), fingerprint);
        Cache[key] = new Entry(payloadJson, DateTime.UtcNow);
    }

    /// <summary>Drop all entries for a file (any scope/fingerprint).</summary>
    public static void InvalidatePath(string filePath)
    {
        var needle = "\u001f" + Normalize(filePath) + "\u001f";
        foreach (var key in Cache.Keys)
        {
            if (key.Contains(needle, StringComparison.OrdinalIgnoreCase))
                Cache.TryRemove(key, out _);
        }
    }

    public static void InvalidateAll() => Cache.Clear();

    static string Normalize(string path) => Path.GetFullPath(path.Trim());
}
