using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
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
