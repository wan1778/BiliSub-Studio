using System.Globalization;
using System.Text.Json;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.OcrGoldHarness;

internal sealed record GoldManifest(
    int SchemaVersion,
    string CorpusId,
    string Description,
    GoldThresholds Thresholds,
    IReadOnlyList<GoldCase> Cases,
    string ManifestPath)
{
    public static GoldManifest Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new MissingInputException($"Gold manifest does not exist: {path}");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException error)
        {
            throw new ManifestException($"Manifest is not valid JSON: {error.Message}", error);
        }

        using (document)
        {
            var root = RequireObject(document.RootElement, "$");
            RequireExactProperties(root, "$", "schemaVersion", "corpusId", "description", "thresholds", "cases");
            var schema = RequireInt(root, "schemaVersion", "$");
            if (schema != 1) throw new ManifestException($"$.schemaVersion must be 1, got {schema}.");
            var corpusId = RequireNonBlank(root, "corpusId", "$");
            var description = RequireNonBlank(root, "description", "$");
            var thresholds = ParseThresholds(RequireProperty(root, "thresholds", "$"));
            var casesElement = RequireProperty(root, "cases", "$");
            if (casesElement.ValueKind != JsonValueKind.Array || casesElement.GetArrayLength() == 0)
                throw new ManifestException("$.cases must be a non-empty array.");
            var cases = casesElement.EnumerateArray().Select((item, index) => ParseCase(item, index)).ToArray();
            EnsureUnique(cases.Select(item => item.Id), "case id");
            return new GoldManifest(schema, corpusId, description, thresholds, cases, path);
        }
    }

    private static GoldThresholds ParseThresholds(JsonElement element)
    {
        var value = RequireObject(element, "$.thresholds");
        RequireExactProperties(value, "$.thresholds", "cueMatchToleranceSeconds", "ptsEpsilonSeconds",
            "minimumCueRecall", "minimumCuePrecision", "maximumCer", "maximumMeanOnsetErrorSeconds",
            "maximumMeanOffsetErrorSeconds", "maximumOnsetErrorFrames", "maximumOffsetErrorFrames");
        var result = new GoldThresholds(
            RequireFinite(value, "cueMatchToleranceSeconds", "$.thresholds"),
            RequireFinite(value, "ptsEpsilonSeconds", "$.thresholds"),
            RequireFinite(value, "minimumCueRecall", "$.thresholds"),
            RequireFinite(value, "minimumCuePrecision", "$.thresholds"),
            RequireFinite(value, "maximumCer", "$.thresholds"),
            RequireFinite(value, "maximumMeanOnsetErrorSeconds", "$.thresholds"),
            RequireFinite(value, "maximumMeanOffsetErrorSeconds", "$.thresholds"),
            RequireInt(value, "maximumOnsetErrorFrames", "$.thresholds"),
            RequireInt(value, "maximumOffsetErrorFrames", "$.thresholds"));
        if (result.CueMatchToleranceSeconds <= 0 || result.PtsEpsilonSeconds <= 0)
            throw new ManifestException("Timing tolerances must be greater than zero.");
        if (result.MinimumCueRecall is < 0 or > 1 || result.MinimumCuePrecision is < 0 or > 1)
            throw new ManifestException("Recall and precision thresholds must be in [0, 1].");
        if (result.MaximumCer < 0 || result.MaximumMeanOnsetErrorSeconds < 0 || result.MaximumMeanOffsetErrorSeconds < 0)
            throw new ManifestException("Maximum score thresholds cannot be negative.");
        if (result.MaximumOnsetErrorFrames < 0 || result.MaximumOffsetErrorFrames < 0)
            throw new ManifestException("Maximum frame-error thresholds cannot be negative.");
        return result;
    }

    private static GoldCase ParseCase(JsonElement element, int index)
    {
        var path = $"$.cases[{index}]";
        var value = RequireObject(element, path);
        RequireExactProperties(value, path, "id", "video", "videoSha256", "provenance", "scan", "expectedCues");
        var id = RequireNonBlank(value, "id", path);
        var video = RequireNonBlank(value, "video", path);
        if (Path.IsPathRooted(video) || video.Split('/', '\\').Any(part => part == ".."))
            throw new ManifestException($"{path}.video must be a relative path without '..'.");
        var sha = RequireNonBlank(value, "videoSha256", path).ToLowerInvariant();
        if (sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
            throw new ManifestException($"{path}.videoSha256 must contain exactly 64 hexadecimal characters.");
        var provenance = ParseProvenance(RequireProperty(value, "provenance", path), path + ".provenance");
        var scan = ParseScan(RequireProperty(value, "scan", path), path + ".scan");
        var cuesElement = RequireProperty(value, "expectedCues", path);
        if (cuesElement.ValueKind != JsonValueKind.Array || cuesElement.GetArrayLength() == 0)
            throw new ManifestException($"{path}.expectedCues must be a non-empty array.");
        var cues = cuesElement.EnumerateArray().Select((item, cueIndex) => ParseCue(item, $"{path}.expectedCues[{cueIndex}]")).ToArray();
        EnsureUnique(cues.Select(cue => cue.Id), $"cue id in case '{id}'");
        for (var cueIndex = 1; cueIndex < cues.Length; cueIndex++)
        {
            if (cues[cueIndex].OnsetPts <= cues[cueIndex - 1].OnsetPts)
                throw new ManifestException($"{path}.expectedCues must be strictly ordered by onsetPts.");
        }
        return new GoldCase(id, video.Replace('/', Path.DirectorySeparatorChar), sha, provenance, scan, cues);
    }

    private static GoldProvenance ParseProvenance(JsonElement element, string path)
    {
        var value = RequireObject(element, path);
        RequireExactProperties(value, path, "kind", "source", "license", "attribution", "sourceUrl");
        var kind = RequireNonBlank(value, "kind", path).ToLowerInvariant();
        if (kind is not ("public" or "generated" or "private"))
            throw new ManifestException($"{path}.kind must be public, generated, or private.");
        var source = RequireNonBlank(value, "source", path);
        var license = RequireNonBlank(value, "license", path);
        var attribution = RequireNonBlank(value, "attribution", path);
        var sourceUrl = RequireNullableString(value, "sourceUrl", path);
        var validSourceUrl = sourceUrl is not null && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
        if (sourceUrl is not null && !validSourceUrl)
            throw new ManifestException($"{path}.sourceUrl must be null or an absolute HTTP(S) URL.");
        if (kind == "public" && !validSourceUrl)
            throw new ManifestException($"{path}.sourceUrl must be an absolute HTTP(S) URL for public data.");
        if (kind == "generated" && !source.StartsWith("generator:", StringComparison.Ordinal))
            throw new ManifestException($"{path}.source must identify a deterministic generator using 'generator:'.");
        return new GoldProvenance(kind, source, license, attribution, sourceUrl);
    }

    private static GoldScan ParseScan(JsonElement element, string path)
    {
        var value = RequireObject(element, path);
        RequireExactProperties(value, path, "startSeconds", "durationSeconds", "region", "mode", "device", "parallelism", "sensitivity");
        var regionElement = RequireObject(RequireProperty(value, "region", path), path + ".region");
        RequireExactProperties(regionElement, path + ".region", "x", "y", "width", "height");
        var region = new OcrRegion(
            RequireFinite(regionElement, "x", path + ".region"),
            RequireFinite(regionElement, "y", path + ".region"),
            RequireFinite(regionElement, "width", path + ".region"),
            RequireFinite(regionElement, "height", path + ".region"));
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X + region.Width > 1 || region.Y + region.Height > 1)
            throw new ManifestException($"{path}.region must be contained in normalized [0, 1] coordinates.");
        var start = RequireFinite(value, "startSeconds", path);
        var duration = RequireFinite(value, "durationSeconds", path);
        var mode = RequireNonBlank(value, "mode", path).ToLowerInvariant();
        var device = RequireNonBlank(value, "device", path).ToLowerInvariant();
        var parallelism = RequireNonBlank(value, "parallelism", path);
        var sensitivity = RequireFinite(value, "sensitivity", path);
        if (start < 0 || duration <= 0) throw new ManifestException($"{path} start/duration is invalid.");
        if (mode is not ("accurate" or "balanced" or "fast")) throw new ManifestException($"{path}.mode is invalid.");
        if (device is not ("cpu" or "gpu" or "hybrid")) throw new ManifestException($"{path}.device is invalid.");
        if (parallelism is not ("1" or "2" or "4"))
            throw new ManifestException($"{path}.parallelism must be one of '1', '2' or '4'.");
        if (sensitivity is < 0.60 or > 1.50) throw new ManifestException($"{path}.sensitivity must be in [0.60, 1.50].");
        return new GoldScan(start, duration, region, mode, device, parallelism, sensitivity);
    }

    private static GoldCue ParseCue(JsonElement element, string path)
    {
        var value = RequireObject(element, path);
        RequireExactProperties(value, path, "id", "onsetPts", "offsetPts", "text");
        var cue = new GoldCue(
            RequireNonBlank(value, "id", path),
            RequireLong(value, "onsetPts", path),
            RequireLong(value, "offsetPts", path),
            RequireNonBlank(value, "text", path));
        if (cue.OffsetPts <= cue.OnsetPts) throw new ManifestException($"{path}.offsetPts must be greater than onsetPts.");
        if (!ChineseSubtitleNormalizer.TryNormalize(cue.Text, out _))
            throw new ManifestException($"{path}.text is rejected by the production Chinese subtitle normalizer.");
        return cue;
    }

    private static JsonElement RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ManifestException($"{path} must be an object.");
        return value;
    }

    private static void RequireExactProperties(JsonElement value, string path, params string[] names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name)) throw new ManifestException($"{path} contains unknown property '{property.Name}'.");
            if (!seen.Add(property.Name)) throw new ManifestException($"{path} contains duplicate property '{property.Name}'.");
        }
        var missing = expected.Except(seen, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new ManifestException($"{path} is missing required properties: {string.Join(", ", missing)}.");
    }

    private static JsonElement RequireProperty(JsonElement value, string name, string path) =>
        value.TryGetProperty(name, out var property) ? property : throw new ManifestException($"{path}.{name} is required.");

    private static string RequireNonBlank(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new ManifestException($"{path}.{name} must be a non-empty string.");
        return property.GetString()!;
    }

    private static string? RequireNullableString(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new ManifestException($"{path}.{name} must be null or a non-empty string.");
        return property.GetString();
    }

    private static double RequireFinite(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var result) || !double.IsFinite(result))
            throw new ManifestException($"{path}.{name} must be a finite number.");
        return result;
    }

    private static int RequireInt(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
            throw new ManifestException($"{path}.{name} must be an integer.");
        return result;
    }

    private static long RequireLong(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
            throw new ManifestException($"{path}.{name} must be an integer.");
        return result;
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ManifestException($"Duplicate {label}: '{duplicate.Key}'.");
    }
}

internal sealed record GoldThresholds(
    double CueMatchToleranceSeconds,
    double PtsEpsilonSeconds,
    double MinimumCueRecall,
    double MinimumCuePrecision,
    double MaximumCer,
    double MaximumMeanOnsetErrorSeconds,
    double MaximumMeanOffsetErrorSeconds,
    int MaximumOnsetErrorFrames,
    int MaximumOffsetErrorFrames);

internal sealed record GoldCase(
    string Id,
    string Video,
    string VideoSha256,
    GoldProvenance Provenance,
    GoldScan Scan,
    IReadOnlyList<GoldCue> ExpectedCues);

internal sealed record GoldProvenance(string Kind, string Source, string License, string Attribution, string? SourceUrl);
internal sealed record GoldScan(double StartSeconds, double DurationSeconds, OcrRegion Region, string Mode, string Device, string Parallelism, double Sensitivity);
internal sealed record GoldCue(string Id, long OnsetPts, long OffsetPts, string Text);

internal sealed class ManifestException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class MissingInputException(string message, Exception? inner = null) : IOException(message, inner);
