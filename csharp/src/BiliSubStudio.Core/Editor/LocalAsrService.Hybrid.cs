using System.Globalization;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalAsrService
{
    private async Task<(AsrCheckpoint Checkpoint, int Retained)> TranscribeHybridAsync(
        AppJob job, EditorAsrRequest request, LocalAsrRuntime runtime, AsrSelection selection,
        string ffmpeg, string operationRoot, string checkpointPath, AsrCheckpoint checkpoint, OwnedProcessGroup processes)
    {
        // Single-device checkpoints have a different file/key. A hybrid frontier
        // advances only after all words through a reconciled seam are durable.
        if (!double.IsFinite(checkpoint.Frontier) || checkpoint.Frontier < 0 || checkpoint.Frontier > request.Duration + .1
            || checkpoint.Cues.Any(cue => cue.End > checkpoint.Frontier + .05))
            checkpoint = AsrCheckpoint.New(checkpoint.Key);
        checkpoint = checkpoint with { Device = "hybrid", ComputeType = selection.ComputeType };
        var retained = checkpoint.Cues.Count;
        if (checkpoint.Complete && checkpoint.Frontier >= request.Duration - .05) return (checkpoint, retained);
        checkpoint = checkpoint with { Complete = false };
        var resumeStart = checkpoint.Frontier;
        // Match FFmpeg's millisecond seek while preserving the exact seam in
        // --core-start, so a resumed first word cannot fall behind the frontier.
        var extractStart = Math.Round(Math.Max(0, resumeStart - 2), 3, MidpointRounding.AwayFromZero);
        var audio = Path.Combine(operationRoot, "hybrid-source.wav");
        job.Set("asr-extract", 27, $"Đang chuẩn bị audio Hybrid từ {Time(resumeStart)}; giữ 2 giây ngữ cảnh nối...");
        await ExtractAudioAsync(ffmpeg, request.SourcePath, audio, extractStart, null, processes, job.CancellationToken);
        var arguments = WorkerArguments(runtime, audio, selection, extractStart, probe: false).ToList();
        arguments.AddRange(["--core-start", resumeStart.ToString("R", CultureInfo.InvariantCulture)]);
        var staged = new List<AsrCue>();
        var ready = false;
        var complete = false;
        var expectedChunk = 0;
        var result = await _processes.RunStreamingAsync(runtime.Python, arguments, job.CancellationToken,
            async (line, _) =>
            {
                if (!TryParseEvent(line, out var parsed)) return;
                using (parsed)
                {
                    var root = parsed.RootElement;
                    switch (GetString(root, "event"))
                    {
                        case "ready":
                            if (ready || complete || GetString(root, "device") != "hybrid") throw new InvalidDataException("Hybrid worker trả sai trạng thái thiết bị.");
                            ready = true;
                            break;
                        case "segment":
                            var raw = ParseCue(root);
                            var previousEnd = staged.Count > 0 ? staged[^1].End : checkpoint.Frontier;
                            if (!ready || complete || !ValidHybridCue(raw)
                                || !root.TryGetProperty("words", out var wordArray)
                                || wordArray.ValueKind != System.Text.Json.JsonValueKind.Array || wordArray.GetArrayLength() != raw.Words.Count)
                                throw new InvalidDataException("Word timing Hybrid không hợp lệ.");
                            if (raw.Start < previousEnd)
                                throw new InvalidDataException("Không ghép được word timing tại ranh giới Hybrid; giữ checkpoint trước đó.");
                            staged.Add(raw);
                            break;
                        case "chunk_complete":
                            var frontier = GetDouble(root, "frontier");
                            if (!ready || complete || GetDouble(root, "index") != expectedChunk
                                || !double.IsFinite(GetDouble(root, "start"))
                                || Math.Abs(GetDouble(root, "start") - checkpoint.Frontier) > .001
                                || !double.IsFinite(frontier) || frontier <= checkpoint.Frontier || frontier > request.Duration + .25
                                || staged.Any(item => item.End > frontier)
                                || checkpoint.Cues.Count + staged.Count > EditorSubtitleDocument.MaxCues)
                                throw new InvalidDataException("Hybrid có khoảng trống hoặc frontier không hợp lệ; không đánh dấu hoàn tất.");
                            checkpoint.Cues.AddRange(staged);
                            checkpoint = checkpoint with { Frontier = frontier };
                            await SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None);
                            staged.Clear();
                            expectedChunk++;
                            job.Set("asr-transcribe-hybrid", 34 + Math.Clamp(frontier / request.Duration, 0, 1) * 62,
                                $"ASR Hybrid · CPU {GetDouble(root, "cpu_chunks"):0} / GPU {GetDouble(root, "gpu_chunks"):0} đoạn · {Time(frontier)}/{Time(request.Duration)} · đã lưu điểm nối an toàn.");
                            break;
                        case "complete":
                            if (!ready || complete || staged.Count != 0 || GetDouble(root, "chunks") != expectedChunk
                                || GetDouble(root, "segments") != checkpoint.Cues.Count - retained
                                || GetDouble(root, "words") != checkpoint.Cues.Skip(retained).Sum(item => item.Words.Count)
                                || !double.IsFinite(GetDouble(root, "latest")) || Math.Abs(GetDouble(root, "latest") - checkpoint.Frontier) > .001)
                                throw new InvalidDataException("Hybrid kết thúc trước khi ghép đủ các đoạn.");
                            complete = true;
                            break;
                    }
                }
            }, runtime.Environment, processes);
        if (result.ExitCode != 0 || !ready || !complete)
            throw new InvalidOperationException("ASR Hybrid dừng; phần đã ghép vẫn được giữ: " + LastLine(result.StandardError));
        return (checkpoint, retained);
    }

    private static bool ValidHybridCue(AsrCue cue) => ValidCue(cue)
        && double.IsFinite(cue.AverageLogProbability) && double.IsFinite(cue.NoSpeechProbability)
        && cue.Words.Count > 0 && cue.Words.All(word => word.Start >= cue.Start && word.End <= cue.End)
        && !cue.Words.Zip(cue.Words.Skip(1)).Any(pair => pair.Second.Start < pair.First.Start);
}
