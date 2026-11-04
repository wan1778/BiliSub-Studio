using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
    private const int MaxSentenceGroupCues = 512;
    // Group admission tolerates small positive gaps (rapid dialogue separated by
    // a breath beat) so a passage shares time. Must mirror the TTS worker's
    // MAX_GROUP_GAP_FRAMES exactly (frames @22050 Hz); 5512 == 0.25s, the same
    // magnitude as the overlap bound below.
    private const long MaxSentenceGroupGapFrames = 5512;
    private const long MaxSentenceGroupOverlapFrames = 5512;
    private const long MaxSentenceGroupDurationFrames = 6615000;
    // Pace rule (01/10 + micro-band 02/10 2.0x; the user tried 07/10 and
    // raised it to 2.5x, the user 25/10 raised it to 3.0x): speeding up may never exceed 1.5x on any path, EXCEPT
    // a micro cue (window <1.1s) which may go to 3.0x; a wide cue stretches at most 1.4x
    // of its duration (pace not below 1/1.4). The record ceiling is widened by 0.01
    // (1.51 / micro 3.01) to subtract the worker's FIT_HEADROOM + retry margin.
    // Mirror the worker exactly by window, with the reason commented.
    private const double VieneuMaxTempoFactor = 1.51;
    private const double VieneuMicroMaxTempoFactor = 3.01;
    private const long VieneuMicroWindowFrames = 24255; // 1.1s @22050 Hz
    // Overflow tran khung (user duyet cho cue don qua dai: doc nguyen cau toi
    // tran window-cap, chi de len khe trong THAT toi cue ke). Day la tran tuyet
    // doi; ReadResultAsync con kiem tra ClipEndSample <= cue ke (mirror worker
    // overflow_budget_frames) de khong bao gio chap nhan doc lap.
    private const long OverflowBudgetFrames = 66150; // 3.0s @22050 Hz
    private const double VieneuMinTempoFactor = 1.0 / 1.4;

    private static double VieneuMaxFactorFor(long targetFrames) =>
        targetFrames is > 0 and < VieneuMicroWindowFrames ? VieneuMicroMaxTempoFactor : VieneuMaxTempoFactor;

    private static bool ValidateNativeSynthesis(TtsWorkerCue cue, long targetFrames, bool naturalSample, int sampleRate, int index)
    {
        // Silence quarantine: no speech could be synthesized for this cue (garbage text or an
        // unfillable window). The timeline stays intact because every sample is real silence of
        // exactly the window length; the cue always needs review.
        if (cue.FitMethod == "silence-fallback")
        {
            if (cue.SourceFrames != 0 || cue.GeneratedFrames != 0 || cue.TrimmedSilenceFrames != 0
                || cue.PaddingFrames != targetFrames || cue.Frames != targetFrames
                || cue.TempoFactor != 0 || cue.TempoInputFrames != 0 || cue.TempoAttempts != 0
                || cue.SynthesisAttempts < 1 || cue.SynthesisAttempts > 12
                || cue.NativeReferenceFrames != 0 || cue.GroupNativeFrames != 0 || cue.ActualSpeedFactor != 0
                || naturalSample || cue.Status != "review" || !cue.VoiceReview)
                throw new InvalidDataException("Voice silence-fallback không hợp lệ; không nhận master này.");
            return true;
        }
        var precisionPaddingBudget = Math.Max(1L, Math.Min((long)Math.Round(.04 * sampleRate), targetFrames / 50));
        var relativeScale = cue.LengthScale / cue.BaseLengthScale;
        var sentenceGroup = cue.TimingSource == "sentence-group";
        const string atempoMethod = "vieneu-atempo";
        var tempoFallback = cue.FitMethod == atempoMethod;
        const double minimumScale = .30;
        if (cue.FitMethod is not ("vieneu-natural" or "vieneu-atempo")
            || !double.IsFinite(cue.BaseLengthScale) || cue.BaseLengthScale <= 0
            || !double.IsFinite(cue.LengthScale) || !double.IsFinite(relativeScale) || relativeScale < minimumScale || relativeScale > 1.20
            || cue.SourceFrames <= 0 || cue.GeneratedFrames <= 0 || cue.GeneratedFrames > cue.Frames
            || cue.TrimmedSilenceFrames < 0 || cue.TrimmedSilenceFrames >= cue.SourceFrames
            || cue.PaddingFrames < 0 || cue.PaddingFrames >= targetFrames || cue.GeneratedFrames != cue.Frames - cue.PaddingFrames
            || cue.SynthesisAttempts < 1 || cue.SynthesisAttempts > 12
            // 10/10-11/10 cases of falsely failed masters (cue 3996 calls 17/5, cue 1274
            // calls 11/1, cue 5841 calls 13/1): synthesis_calls is NOT the number of
            // inferences as its name suggests — it counts every on_attempt loop, including the loops
            // that reuse a batch without new synthesis (Phase-2 condense: the batch is condensed once
            // and the loop still counts each variant). Every upper bound placed on it (absolute
            // +10, the 6x ratio, both forms) is guesswork fitted case by case:
            // each new case breaks the previous case's bound (13/1 slipped past every bound, and the
            // earlier failing cues of 13-35/1 also slipped past). Drop the upper bound entirely, keep only the lower
            // bound (the total loop count cannot be less than the final phase's attempts) + attempts
            // within 1..12 + cache-hit means calls==0. Genuine protection against
            // runaway loops lives in the worker (every retry loop has a ceiling, and the assertion
            // phase_calls==attempts raises RuntimeError when counts disagree), while AUDIO
            // correctness is already covered by independent frames/SHA/pace/tempo/speed/review bounds
            // — no case of corrupt audio has ever been caught by this calls
            // bound alone.
            || (cue.CacheHit ? cue.SynthesisCalls != 0 : cue.SynthesisCalls < cue.SynthesisAttempts)
            || (naturalSample && cue.TimingSource != "sample"))
            throw new InvalidDataException($"Voice không có metadata nhịp đọc VieNeu hợp lệ; không nhận master này (cue {index} {cue.Id}).");
        if (cue.NativeReferenceFrames <= 0 || !double.IsFinite(cue.ActualSpeedFactor)
            || cue.ActualSpeedFactor < VieneuMinTempoFactor - 1e-9 || cue.ActualSpeedFactor > VieneuMaxFactorFor(targetFrames)
            || (sentenceGroup
                ? cue.GroupNativeFrames != cue.NativeReferenceFrames
                : cue.GroupNativeFrames != 0
                    || Math.Abs(cue.NativeReferenceFrames / (double)cue.GeneratedFrames - cue.ActualSpeedFactor) > 1e-9))
            throw new InvalidDataException($"Voice không có giới hạn tốc độ đọc thực đo hợp lệ (cue {index} {cue.Id}).");
        if (cue.SynthesisAttempts == 1
            && (cue.LengthScale != cue.BaseLengthScale
                || Math.Abs(cue.RawDuration - cue.SourceFrames / (double)sampleRate) > 1e-9
                // 30/09 (cue5 VieNeu falsely silent case): atempo on the very first pass only happens
                // when there is no scale loop — native != generated at the tempo record
                // is honest. Exempt ONLY for vieneu-atempo.
                || (!sentenceGroup && cue.FitMethod != atempoMethod && cue.NativeReferenceFrames != cue.GeneratedFrames)))
            throw new InvalidDataException($"Voice lượt tự nhiên không có metadata VieNeu gốc hợp lệ (cue {index} {cue.Id}).");
        if (tempoFallback
            ? cue.TempoInputFrames != cue.SourceFrames - cue.TrimmedSilenceFrames
                || cue.TempoInputFrames <= 0
                || !double.IsFinite(cue.TempoFactor)
                || cue.TempoFactor < VieneuMinTempoFactor - 1e-9 || cue.TempoFactor > VieneuMaxFactorFor(targetFrames)
                || Math.Abs(cue.TempoInputFrames / (double)cue.GeneratedFrames - cue.TempoFactor) > 1e-9
                || cue.TempoAttempts is < 1 or > 6
            : cue.TempoFactor != 0 || cue.TempoInputFrames != 0 || cue.TempoAttempts != 0
                || cue.SourceFrames - cue.TrimmedSilenceFrames != cue.GeneratedFrames)
            throw new InvalidDataException($"Voice không bảo toàn đầy đủ chuỗi thoại hoặc metadata thời lượng (cue {index} {cue.Id}).");
        return tempoFallback || sentenceGroup || relativeScale is < .70 or > 1
            || cue.TrimmedSilenceFrames > 0 || cue.PaddingFrames > precisionPaddingBudget;
    }

    private static void ValidateSentenceGroupWindows(
        IReadOnlyList<TtsWorkerCue> actual,
        IReadOnlyList<TtsCueManifest> expected,
        int sampleRate)
    {
        for (var first = 0; first < actual.Count;)
        {
            var last = first;
            var groupStart = checked((long)Math.Round(expected[first].CueStart * sampleRate));
            while (last + 1 < expected.Count && last - first + 1 < MaxSentenceGroupCues)
            {
                var gap = checked((long)Math.Round(expected[last + 1].CueStart * sampleRate))
                    - checked((long)Math.Round(expected[last].CueEnd * sampleRate));
                var candidateEnd = checked((long)Math.Round(expected[last + 1].CueEnd * sampleRate));
                if (expected[last].TimingSource == "sample" || expected[last + 1].TimingSource == "sample"
                    || gap < -MaxSentenceGroupOverlapFrames || gap > MaxSentenceGroupGapFrames
                    || candidateEnd - groupStart > MaxSentenceGroupDurationFrames)
                    break;
                last++;
            }
            var grouped = 0;
            for (var item = first; item <= last; item++)
                if (actual[item].TimingSource == "sentence-group") grouped++;
            if (grouped == 0)
            {
                first = last + 1;
                continue;
            }
            if (last == first || grouped != last - first + 1)
                throw new InvalidDataException("Nhóm câu TTS không hợp lệ.");
            var groupEnd = checked((long)Math.Round(expected[last].CueEnd * sampleRate));
            if (groupEnd - groupStart > MaxSentenceGroupDurationFrames
                || actual[first].ClipStartSample != groupStart
                || checked(actual[last].ClipStartSample + actual[last].TargetFrames) != groupEnd)
                throw new InvalidDataException("Nhóm câu TTS vượt biên timecode nguồn.");
            var generatedFrames = 0L;
            for (var item = first; item <= last; item++)
            {
                if (actual[item].TargetFrames <= 0
                    || (item > first && actual[item].ClipStartSample
                        != actual[item - 1].ClipStartSample + actual[item - 1].TargetFrames)
                    || actual[item].GroupNativeFrames != actual[first].GroupNativeFrames
                    || actual[item].ActualSpeedFactor != actual[first].ActualSpeedFactor)
                    throw new InvalidDataException("Nhóm câu TTS không phủ liên tục timecode nguồn.");
                generatedFrames = checked(generatedFrames + actual[item].GeneratedFrames);
            }
            var measuredSpeed = actual[first].GroupNativeFrames / (double)generatedFrames;
            if (Math.Abs(measuredSpeed - actual[first].ActualSpeedFactor) > 1e-9
                || measuredSpeed < VieneuMinTempoFactor - 1e-9 || measuredSpeed > VieneuMaxFactorFor(groupEnd - groupStart))
                throw new InvalidDataException("Nhóm câu TTS vượt giới hạn tốc độ đọc rõ.");
            first = last + 1;
        }
    }

    // Verify PCM frame count from the actual WAV, not only the worker's duration.
    // FFmpeg may include LIST/JUNK chunks; never assume a fixed 44-byte header.
    private static long ReadClipFrames(string path, int sampleRate)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF"
            || (long)reader.ReadUInt32() + 8 != stream.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("WAV voice thiếu hoặc sai RIFF header.");
        var format = false;
        long frames = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end + (size & 1) > stream.Length) throw new InvalidDataException("WAV voice bị thiếu dữ liệu.");
            if (name == "fmt ")
            {
                if (format || size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1
                    || reader.ReadUInt32() != sampleRate || reader.ReadUInt32() != sampleRate * 2
                    || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("WAV voice không phải mono PCM16 đúng sample rate.");
                format = true;
            }
            else if (name == "data")
            {
                if (frames >= 0 || size == 0 || size % 2 != 0) throw new InvalidDataException("WAV voice sai số mẫu PCM.");
                frames = size / 2;
            }
            stream.Position = end + (size & 1);
        }
        if (!format || frames <= 0 || stream.Position != stream.Length) throw new InvalidDataException("WAV voice không hoàn chỉnh.");
        return frames;
    }
}
