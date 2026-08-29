using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
    private static bool ValidateNativeSynthesis(TtsWorkerCue cue, long targetFrames, bool naturalSample, int sampleRate)
    {
        var paddingBudget = Math.Max(1L, Math.Min((long)Math.Round(.04 * sampleRate), targetFrames / 50));
        var relativeScale = cue.LengthScale / cue.BaseLengthScale;
        if (cue.FitMethod != "piper-length-scale" || !double.IsFinite(cue.BaseLengthScale) || cue.BaseLengthScale <= 0
            || !double.IsFinite(cue.LengthScale) || !double.IsFinite(relativeScale) || relativeScale is < .85 or > 1.20
            || cue.GeneratedFrames <= 0 || cue.GeneratedFrames > cue.Frames
            || cue.PaddingFrames < 0 || cue.PaddingFrames > paddingBudget || cue.GeneratedFrames != cue.Frames - cue.PaddingFrames
            || cue.SynthesisAttempts is < 1 or > 10 || cue.SynthesisCalls != (cue.CacheHit ? 0 : cue.SynthesisAttempts)
            || (cue.SynthesisAttempts == 1 && (cue.LengthScale != cue.BaseLengthScale
                || Math.Abs(cue.RawDuration - cue.GeneratedFrames / (double)sampleRate) > 1e-9))
            || (naturalSample && (cue.PaddingFrames != 0 || cue.SynthesisAttempts != 1)))
            throw new InvalidDataException("Voice không có metadata nhịp đọc Piper hợp lệ hoặc bù im lặng quá nhiều; không nhận master này.");
        return relativeScale is < .90 or > 1.15;
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
