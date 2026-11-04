using System.Text;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Peak envelope of a voice file for the timeline waveform: one 0-255 peak per
/// 20 ms bucket. Built once through ffmpeg (mono 2 kHz s16le - hours of audio
/// stay cheap) and cached next to the source file as a small self-describing
/// .bin, so every later timeline render skips ffmpeg entirely.
/// </summary>
public sealed record EditorVoiceWaveform(double BucketSeconds, double DurationSeconds, byte[] Peaks)
{
    private const string CachePrefix = "waveform-";
    private const int CacheVersion = 1;
    private const int BucketMilliseconds = 20;
    private const int DecodeRate = 2000;
    private const int SamplesPerBucket = DecodeRate * BucketMilliseconds / 1000;
    private static readonly byte[] CacheMagic = "BSWV"u8.ToArray();
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Cache file of the envelope of <paramref name="audioPath"/>. The name
    /// carries the voice file's size and write time, so a regenerated voice
    /// never reuses the peaks of the old one.
    /// </summary>
    public static string CachePathFor(string audioPath)
    {
        var full = Path.GetFullPath(audioPath);
        var info = new FileInfo(full);
        var name = $"{CachePrefix}{(info.Exists ? info.Length : 0)}-{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}.bin";
        return Path.Combine(Path.GetDirectoryName(full) ?? ".", name);
    }

    /// <summary>Reads a cached envelope; null when it is missing or corrupt (the caller rebuilds).</summary>
    public static EditorVoiceWaveform? TryLoadCache(string audioPath)
    {
        try
        {
            var path = CachePathFor(audioPath);
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The cached envelope when present, otherwise decoded through ffmpeg and written back to the cache (best effort).</summary>
    public static async Task<EditorVoiceWaveform?> LoadOrBuildAsync(
        ToolManager tools,
        ProcessRunner processes,
        string audioPath,
        CancellationToken cancellationToken)
    {
        var cached = TryLoadCache(audioPath);
        if (cached is not null) return cached;
        var envelope = await DecodeAsync(tools, processes, audioPath, cancellationToken);
        if (envelope is null) return null;
        try
        {
            var path = CachePathFor(audioPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Serialize(envelope), cancellationToken);
        }
        catch
        {
            // A read-only folder only costs the next decode, never the render.
        }
        return envelope;
    }

    // ffmpeg -> mono 2 kHz s16le on stdout. CaptureBytesAsync (binary) only:
    // RunAsync would mangle the bytes through strings. A waveform is a
    // decoration, so any failure returns null instead of breaking the timeline.
    private static async Task<EditorVoiceWaveform?> DecodeAsync(
        ToolManager tools,
        ProcessRunner processes,
        string audioPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var ffmpeg = await tools.EnsureFfmpegAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DecodeTimeout);
            var pcm = await processes.CaptureBytesAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin",
                 "-i", Path.GetFullPath(audioPath),
                 "-map", "0:a:0", "-ac", "1", "-ar", DecodeRate.ToString(),
                 "-f", "s16le", "-"],
                timeout.Token);
            var peaks = ComputePeaks(pcm, DecodeRate, BucketMilliseconds);
            if (peaks.Length == 0) return null;
            return new EditorVoiceWaveform(
                BucketMilliseconds / 1000.0,
                peaks.Length * BucketMilliseconds / 1000.0,
                peaks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Widest sample of every bucket: the shape a timeline waveform needs (silence flat, speech spiky).</summary>
    internal static byte[] ComputePeaks(byte[] pcm, int rate, int bucketMilliseconds)
    {
        var perBucket = Math.Max(1, rate * bucketMilliseconds / 1000);
        var count = pcm.Length / 2 / perBucket;
        var peaks = new byte[count];
        for (var bucket = 0; bucket < count; bucket++)
        {
            var best = 0;
            var start = bucket * perBucket;
            for (var i = 0; i < perBucket; i++)
            {
                var sample = (short)(pcm[(start + i) * 2] | (pcm[(start + i) * 2 + 1] << 8));
                // Negate by hand: Math.Abs(short.MinValue) overflows.
                var magnitude = sample < 0 ? -(int)sample : sample;
                if (magnitude > best) best = magnitude;
            }
            peaks[bucket] = (byte)Math.Min(255, best * 255 / 32768);
        }
        return peaks;
    }

    internal static byte[] Serialize(EditorVoiceWaveform waveform)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(CacheMagic);
            writer.Write(CacheVersion);
            writer.Write(BucketMilliseconds);
            writer.Write(waveform.DurationSeconds);
            writer.Write(waveform.Peaks.Length);
            writer.Write(waveform.Peaks);
        }
        return stream.ToArray();
    }

    internal static EditorVoiceWaveform? Parse(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var magic = reader.ReadBytes(CacheMagic.Length);
            if (magic.Length != CacheMagic.Length || !magic.AsSpan().SequenceEqual(CacheMagic)) return null;
            if (reader.ReadInt32() != CacheVersion) return null;
            var bucketMilliseconds = reader.ReadInt32();
            if (bucketMilliseconds != BucketMilliseconds) return null;
            var duration = reader.ReadDouble();
            var count = reader.ReadInt32();
            if (duration <= 0 || count <= 0 || count > stream.Length) return null;
            var peaks = reader.ReadBytes(count);
            if (peaks.Length != count) return null;
            return new EditorVoiceWaveform(bucketMilliseconds / 1000.0, duration, peaks);
        }
        catch
        {
            return null;
        }
    }
}
