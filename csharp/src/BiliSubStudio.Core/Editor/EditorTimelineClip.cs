using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliSubStudio.Core.Editor;

/// <summary>
/// A video segment in the composition timeline. Each clip holds exactly one source file
/// and the range [SourceIn, SourceIn + Duration] measured within that same source file;
/// its position on the timeline is the total duration of the clips standing before it.
/// The out point is always derived from the in point plus the duration (like source_in in
/// FilmCraft) so the two ends can never drift apart.
/// </summary>
public sealed record EditorTimelineClip(
    string Id,
    string SourcePath,
    double SourceIn,
    double Duration,
    string Label)
{
    /// <summary>Out point in the source file, derived from the in point and the duration.</summary>
    public double End => SourceIn + Math.Max(0, Duration);
}

/// <summary>Composition of the project: the clips in playback order on the timeline.</summary>
public sealed record EditorTimelineProject(IReadOnlyList<EditorTimelineClip> Clips);

/// <summary>
/// Reads both the old (start/end) and the new (source_in/duration) shape of a clip
/// so an already-saved project does not lose its composition when reopened.
/// </summary>
public sealed class EditorTimelineClipJsonConverter : JsonConverter<EditorTimelineClip>
{
    public override EditorTimelineClip Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var id = ReadString(root, "id");
        var path = ReadString(root, "source_path");
        if (string.IsNullOrEmpty(path)) path = ReadString(root, "sourcePath");
        var label = ReadString(root, "label");
        var sourceIn = 0d;
        var duration = 0d;
        if (root.TryGetProperty("source_in", out var newIn)) sourceIn = ReadDouble(newIn);
        else if (root.TryGetProperty("start", out var oldIn)) sourceIn = ReadDouble(oldIn);
        if (root.TryGetProperty("duration", out var newDuration)) duration = ReadDouble(newDuration);
        else if (root.TryGetProperty("end", out var oldEnd)) duration = ReadDouble(oldEnd) - sourceIn;
        return new EditorTimelineClip(id, path, sourceIn, Math.Max(0, duration), label);
    }

    public override void Write(Utf8JsonWriter writer, EditorTimelineClip value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("source_path", value.SourcePath);
        writer.WriteNumber("source_in", value.SourceIn);
        writer.WriteNumber("duration", value.Duration);
        writer.WriteString("label", value.Label);
        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadDouble(JsonElement value)
        => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0d;
}
