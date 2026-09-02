using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
    private const int MaxSentenceGroupCues = 512;
    private const long MaxSentenceGroupGapFrames = 0;
    private const long MaxSentenceGroupOverlapFrames = 5512;
    private const long MaxSentenceGroupDurationFrames = 6615000;

    private static bool ValidateNativeSynthesis(TtsWorkerCue cue, long targetFrames, bool naturalSample, int sampleRate)
    {
        var precisionPaddingBudget = Math.Max(1L, Math.Min((long)Math.Round(.04 * sampleRate), targetFrames / 50));
        var relativeScale = cue.LengthScale / cue.BaseLengthScale;
        var sentenceGroup = cue.TimingSource == "sentence-group";
        var tempoFallback = cue.FitMethod == "piper-atempo";
        const double minimumScale = .30;
        if (cue.FitMethod is not ("piper-length-scale" or "piper-atempo")
            || !double.IsFinite(cue.BaseLengthScale) || cue.BaseLengthScale <= 0
            || !double.IsFinite(cue.LengthScale) || !double.IsFinite(relativeScale) || relativeScale < minimumScale || relativeScale > 1.20
            || cue.SourceFrames <= 0 || cue.GeneratedFrames <= 0 || cue.GeneratedFrames > cue.Frames
            || cue.TrimmedSilenceFrames < 0 || cue.TrimmedSilenceFrames >= cue.SourceFrames
            || cue.PaddingFrames < 0 || cue.PaddingFrames >= targetFrames || cue.GeneratedFrames != cue.Frames - cue.PaddingFrames
            || cue.SynthesisAttempts < 1 || cue.SynthesisAttempts > 12
            || (cue.CacheHit ? cue.SynthesisCalls != 0
                : cue.SynthesisCalls < cue.SynthesisAttempts || cue.SynthesisCalls > cue.SynthesisAttempts + 10)
            || (naturalSample && cue.TimingSource != "sample"))
            throw new InvalidDataException("Voice không có metadata nhịp đọc Piper hợp lệ; không nhận master này.");
        if (cue.NativeReferenceFrames <= 0 || !double.IsFinite(cue.ActualSpeedFactor)
            || cue.ActualSpeedFactor < 1 || cue.ActualSpeedFactor > 100
            || (sentenceGroup
                ? cue.GroupNativeFrames != cue.NativeReferenceFrames
                : cue.GroupNativeFrames != 0
                    || Math.Abs(cue.NativeReferenceFrames / (double)cue.GeneratedFrames - cue.ActualSpeedFactor) > 1e-9))
            throw new InvalidDataException("Voice không có giới hạn tốc độ đọc thực đo hợp lệ.");
        if (cue.SynthesisAttempts == 1
            && (cue.LengthScale != cue.BaseLengthScale
                || Math.Abs(cue.RawDuration - cue.SourceFrames / (double)sampleRate) > 1e-9
                || (!sentenceGroup && cue.NativeReferenceFrames != cue.GeneratedFrames)))
            throw new InvalidDataException("Voice lượt tự nhiên không có metadata Piper gốc hợp lệ.");
        if (tempoFallback
            ? cue.TempoInputFrames != cue.SourceFrames - cue.TrimmedSilenceFrames
                || cue.TempoInputFrames <= cue.GeneratedFrames || !double.IsFinite(cue.TempoFactor)
                || cue.TempoFactor <= 1
                || Math.Abs(cue.TempoInputFrames / (double)cue.GeneratedFrames - cue.TempoFactor) > 1e-9
                || cue.TempoAttempts is < 1 or > 6
            : cue.TempoFactor != 0 || cue.TempoInputFrames != 0 || cue.TempoAttempts != 0
                || cue.SourceFrames - cue.TrimmedSilenceFrames != cue.GeneratedFrames)
            throw new InvalidDataException("Voice không bảo toàn đầy đủ chuỗi thoại hoặc metadata thời lượng.");
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
                || measuredSpeed < 1 || measuredSpeed > 100)
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
