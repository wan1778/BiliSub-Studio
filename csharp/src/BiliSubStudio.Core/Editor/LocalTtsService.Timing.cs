using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
    private const int MaxSentenceGroupCues = 12;
    private const long MaxSentenceGroupGapFrames = 2646;
    private const long MaxSentenceGroupDurationFrames = 264600;

    private static bool ValidateNativeSynthesis(TtsWorkerCue cue, long targetFrames, bool naturalSample, int sampleRate)
    {
        var precisionPaddingBudget = Math.Max(1L, Math.Min((long)Math.Round(.04 * sampleRate), targetFrames / 50));
        var relativeScale = cue.LengthScale / cue.BaseLengthScale;
        var minimumScale = cue.TimingSource == "sentence-group" ? .45 : .85;
        if (cue.FitMethod != "piper-length-scale" || !double.IsFinite(cue.BaseLengthScale) || cue.BaseLengthScale <= 0
            || !double.IsFinite(cue.LengthScale) || !double.IsFinite(relativeScale) || relativeScale < minimumScale || relativeScale > 1.20
            || cue.SourceFrames <= 0 || cue.GeneratedFrames <= 0 || cue.GeneratedFrames > cue.Frames
            || cue.TrimmedSilenceFrames < 0 || cue.TrimmedSilenceFrames >= cue.SourceFrames
            || cue.SourceFrames - cue.TrimmedSilenceFrames != cue.GeneratedFrames
            || cue.PaddingFrames < 0 || cue.PaddingFrames >= targetFrames || cue.GeneratedFrames != cue.Frames - cue.PaddingFrames
            || cue.SynthesisAttempts is < 1 or > 10
            || (cue.CacheHit ? cue.SynthesisCalls != 0
                : cue.SynthesisCalls < cue.SynthesisAttempts || cue.SynthesisCalls > cue.SynthesisAttempts + 10)
            || (cue.SynthesisAttempts == 1 && (cue.LengthScale != cue.BaseLengthScale
                || Math.Abs(cue.RawDuration - cue.SourceFrames / (double)sampleRate) > 1e-9))
            || (naturalSample && (cue.TrimmedSilenceFrames != 0 || cue.PaddingFrames != 0 || cue.SynthesisAttempts != 1)))
            throw new InvalidDataException("Voice không có metadata nhịp đọc Piper hợp lệ; không nhận master này.");
        return cue.TimingSource == "sentence-group" || relativeScale is < .90 or > 1.15 || cue.TrimmedSilenceFrames > 0
            || cue.PaddingFrames > precisionPaddingBudget;
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
                    || gap is < 0 or > MaxSentenceGroupGapFrames || EndsSentence(expected[last].Text)
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
            for (var item = first; item <= last; item++)
            {
                if (actual[item].TargetFrames <= 0
                    || (item > first && actual[item].ClipStartSample
                        != actual[item - 1].ClipStartSample + actual[item - 1].TargetFrames))
                    throw new InvalidDataException("Nhóm câu TTS không phủ liên tục timecode nguồn.");
            }
            first = last + 1;
        }
    }

    private static bool EndsSentence(string text)
    {
        var value = text.AsSpan().TrimEnd();
        while (!value.IsEmpty && value[^1] is '"' or '\'' or '”' or '’' or ')' or ']' or '}')
            value = value[..^1].TrimEnd();
        return !value.IsEmpty && value[^1] is ('.' or '!' or '?' or '…');
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
