from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"missing patch anchor: {label}")
    if text.count(old) != 1:
        raise RuntimeError(f"ambiguous patch anchor: {label} ({text.count(old)})")
    return text.replace(old, new, 1)


editor = Path("csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs")
text = editor.read_text(encoding="utf-8")
if "using System.Text.Json;" in text:
    raise RuntimeError("M5 Json import already present")
text = replace_once(text, "using System.Text;\n", "using System.Text;\nusing System.Text.Json;\n", "Json import")

old_run = '''            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg editor: {result.StandardError.Trim()}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0) throw new InvalidDataException("Video đã render rỗng.");
            job.Set("finalizing", 98, "Đang hoàn tất file...");
            File.Move(temporary, output);
            return new VideoEditResult(output);
'''
new_run = '''            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg editor: {result.StandardError.Trim()}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0) throw new InvalidDataException("Video đã render rỗng.");
            job.Set("validating", 97, "Đang kiểm tra stream, thời lượng và khả năng giải mã...");
            await ValidateRenderedOutputAsync(temporary, request.Duration, voice is not null || audio.SourceMode != "mute", token);
            job.Set("finalizing", 99, "Đã xác minh file; đang hoàn tất...");
            File.Move(temporary, output);
            return new VideoEditResult(output);
'''
text = replace_once(text, old_run, new_run, "RunAsync promotion gate")

preview_marker = "    public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(\n"
if "private async Task ValidateRenderedOutputAsync(" in text:
    raise RuntimeError("M5 validation helper already present")
helper = '''    private async Task ValidateRenderedOutputAsync(
        string path,
        double expectedDuration,
        bool expectAudio,
        CancellationToken cancellationToken)
    {
        var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var probe = await _processes.RunAsync(ffprobe,
        [
            "-v", "error",
            "-show_entries", "stream=codec_type,width,height:format=duration,size",
            "-of", "json", path,
        ], cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidDataException("Không đọc được file vừa render: " + probe.StandardError.Trim());
        ValidateRenderedProbe(probe.StandardOutput, expectedDuration, expectAudio);

        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var videoHead = await _processes.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
            "-i", path, "-map", "0:v:0", "-t", "1.000", "-f", "null", "-",
        ], cancellationToken);
        if (videoHead.ExitCode != 0)
            throw new InvalidDataException("Video đầu ra không giải mã được: " + videoHead.StandardError.Trim());

        if (expectAudio)
        {
            var audioFrame = await _processes.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
                "-i", path, "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-",
            ], cancellationToken);
            if (audioFrame.ExitCode != 0)
                throw new InvalidDataException("Audio đầu ra không giải mã được: " + audioFrame.StandardError.Trim());
        }

        if (expectedDuration > 2.5)
        {
            var videoTail = await _processes.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
                "-sseof", "-2.000", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-",
            ], cancellationToken);
            if (videoTail.ExitCode != 0)
                throw new InvalidDataException("Phần cuối video đầu ra không giải mã được: " + videoTail.StandardError.Trim());
        }
    }

    private static void ValidateRenderedProbe(string json, double expectedDuration, bool expectAudio)
    {
        if (!double.IsFinite(expectedDuration) || expectedDuration <= 0)
            throw new InvalidDataException("Thời lượng nguồn không hợp lệ để xác minh output.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var videoStreams = 0;
        var audioStreams = 0;
        var videoWidth = 0;
        var videoHeight = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeNode) ? typeNode.GetString() : null;
                if (string.Equals(type, "video", StringComparison.Ordinal))
                {
                    videoStreams++;
                    if (stream.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var width)) videoWidth = Math.Max(videoWidth, width);
                    if (stream.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var height)) videoHeight = Math.Max(videoHeight, height);
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal)) audioStreams++;
            }
        }
        if (videoStreams == 0 || videoWidth <= 0 || videoHeight <= 0)
            throw new InvalidDataException("Output không có video stream hợp lệ.");
        if (expectAudio && audioStreams == 0)
            throw new InvalidDataException("Output thiếu audio theo chính sách Keep/Duck/voice Việt.");
        if (!expectAudio && audioStreams != 0)
            throw new InvalidDataException("Output vẫn có audio dù chính sách yêu cầu Mute hoàn toàn.");

        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Output thiếu metadata container.");
        if (!TryJsonDouble(format, "duration", out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Output không có duration hợp lệ.");
        if (!TryJsonDouble(format, "size", out var size) || !double.IsFinite(size) || size <= 0)
            throw new InvalidDataException("Output không có kích thước container hợp lệ.");

        var tolerance = Math.Clamp(expectedDuration * .0005, 1.5, 5.0);
        if (Math.Abs(duration - expectedDuration) > tolerance)
            throw new InvalidDataException($"Duration output lệch {Math.Abs(duration - expectedDuration):0.000}s, vượt tolerance {tolerance:0.000}s.");
    }

    private static bool TryJsonDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Number) return node.TryGetDouble(out value);
        return node.ValueKind == JsonValueKind.String
            && double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

'''
text = replace_once(text, preview_marker, helper + preview_marker, "validation helper insertion")
editor.write_text(text, encoding="utf-8", newline="\n")


tests = Path("csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs")
text = tests.read_text(encoding="utf-8")
old_list = '''        ("voice track mixes identically for keep duck mute", EditorVoiceMixContractAsync),
        ("Vietnamese TTS text normalization stays deterministic", VietnameseTtsNormalizerContractAsync),
'''
new_list = '''        ("voice track mixes identically for keep duck mute", EditorVoiceMixContractAsync),
        ("editor final render validates streams duration and audio policy", EditorRenderValidationContractAsync),
        ("Vietnamese TTS text normalization stays deterministic", VietnameseTtsNormalizerContractAsync),
'''
text = replace_once(text, old_list, new_list, "contract registration")

normalizer_marker = "    private static Task VietnameseTtsNormalizerContractAsync()\n"
if "private static Task EditorRenderValidationContractAsync()" in text:
    raise RuntimeError("M5 render contract already present")
contract = '''    private static Task EditorRenderValidationContractAsync()
    {
        var method = typeof(VideoEditorService).GetMethod("ValidateRenderedProbe", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Editor final-render validation policy");
        const string withAudio = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080},{"codec_type":"audio"}],"format":{"duration":"120.040","size":"1000000"}}
            """;
        const string withoutAudio = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080}],"format":{"duration":"120.010","size":"900000"}}
            """;
        method.Invoke(null, [withAudio, 120d, true]);
        method.Invoke(null, [withoutAudio, 120d, false]);

        void Invalid(string json, double duration, bool expectAudio, string label)
        {
            try
            {
                method.Invoke(null, [json, duration, expectAudio]);
                throw new InvalidOperationException(label + " was accepted");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            {
            }
        }

        Invalid(withoutAudio, 120d, true, "missing required audio");
        Invalid(withAudio, 120d, false, "unexpected muted audio");
        Invalid(withAudio, 100d, true, "duration drift");
        Invalid("""{"streams":[{"codec_type":"audio"}],"format":{"duration":"120","size":"1000"}}""", 120d, true, "missing video");
        return Task.CompletedTask;
    }

'''
text = replace_once(text, normalizer_marker, contract + normalizer_marker, "render contract insertion")
tests.write_text(text, encoding="utf-8", newline="\n")


props = Path("csharp/Directory.Build.props")
text = props.read_text(encoding="utf-8")
text = replace_once(text, "4.0.0-beta.33-csharp-p5", "4.0.0-beta.34-csharp-p5", "technical version")
props.write_text(text, encoding="utf-8", newline="\n")


doc = Path("docs/migration/CSHARP_EDITOR_M5_CALL_MAP.md")
if doc.exists():
    raise RuntimeError("M5 call map already exists")
doc.write_text("""# C# Editor M5 call map

Status: production checkpoint for final-render validation and atomic promotion.

## Final export

```text
EditorPage
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
       -> BuildFilter / BuildAss
       -> BuildVoiceAudioFilter or BuildAudioArguments
       -> FFmpeg renders to sibling .rendering file
       -> ffprobe validates video/audio streams, duration and container size
       -> FFmpeg bounded decode validates video head, optional audio and video tail
       -> File.Move promotes the verified sibling file atomically
  -> AppJob.Finish only after validation and promotion
```

## Audio acceptance

- Keep/Duck require an audio stream in the rendered result.
- Mute without a Vietnamese voice track requires no audio stream.
- Mute with a Vietnamese voice track requires the TTS audio stream.
- Preview/export continue to use the same source-audio and TTS mixing semantics; M5 only adds final-output validation.

## Failure and cancellation

- Source media is never overwritten.
- Failed validation never promotes the `.rendering` file.
- The existing `finally` cleanup removes partial `.rendering` and temporary ASS artifacts.
- Cancellation remains cleanup-aware through the Editor job and ProcessRunner-owned FFmpeg processes.

## Release gate

A successful FFmpeg exit code or non-empty file is not sufficient. The Editor reports completion only after stream/duration/decode validation and atomic promotion.
""", encoding="utf-8", newline="\n")

print("M5_PATCH_OK")
