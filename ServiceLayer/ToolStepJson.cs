using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMcp.ServiceLayer;

/// <summary>
/// Wire JSON for one tool step — same shape as CSX <c>StepResponse</c>
/// (<c>ok</c>, <c>kind</c>, <c>summary</c>, <c>error</c>, <c>data</c>).
/// </summary>
public static class ToolStepJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Ok(string kind, string summary, object? data = null) =>
        JsonSerializer.Serialize(new Envelope(true, kind, summary, null, data), Options);

    public static string Fail(string kind, string error, object? data = null) =>
        JsonSerializer.Serialize(new Envelope(false, kind, error, error, data), Options);

    private sealed record Envelope(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("data")] object? Data);
}
